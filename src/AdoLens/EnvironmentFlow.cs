using System.Text.Json;

namespace AdoLens;

/// <summary>
/// Where code goes in a project, found without asking anyone: release stages deployed in the last half year and the
/// long-lived branches of its Git repositories, ordered DEV → UAT → PROD with a first guess of each name.
/// </summary>
public static class EnvironmentFlow
{
    /// <summary>The effective map of a project: the local one when set, else the team's page in the wiki.</summary>
    public static async Task<(IReadOnlyDictionary<string, string> Map, string Source)> EffectiveAsync(Session session, string project, CancellationToken ct)
    {
        if (session.Config.Environments.Count > 0) return (session.Config.Environments, "local settings");
        if (await session.Team.EnvironmentsAsync(project, ct) is { } team) return (team, $"team page \"{TeamSettings.PagePath}\" in the wiki of {project}");
        return (new Dictionary<string, string>(), "none");
    }

    /// <summary>
    /// Release stages and long-lived branches of the project with the code (the connection's code project, when the
    /// code lives elsewhere than the work items) and of <paramref name="project"/> itself.
    /// </summary>
    public static async Task<List<(string Key, string? Guess)>> DetectAsync(Session session, string project, CancellationToken ct) =>
        (await DetectWithSourcesAsync(session, project, ct)).Select(d => (d.Key, d.Guess)).ToList();

    /// <summary>
    /// As <see cref="DetectAsync"/>, with where each one comes from ("$/Project/Trunk/Main, last changeset 2026-10-20";
    /// "release Deploy, stage 1, deployed 2026-10-24 from …"), so two of the same name can be told apart.
    /// </summary>
    public static async Task<List<(string Key, string? Guess, string Source)>> DetectWithSourcesAsync(Session session, string project, CancellationToken ct)
    {
        var sources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void From(string key, string source)
        {
            if (!sources.TryGetValue(key, out var list)) sources[key] = list = [];
            if (!list.Contains(source)) list.Add(source);
        }
        static string Day(DateTime? d) => d?.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "?";
        var projects = new[] { session.Config.CodeProject, project }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var keys = new List<string>();
        // Long-lived branches of the repositories in use (a commit in the last half year); feature and work branches are
        // filtered by ProposeFlow. With several repositories each branch is named with its repository ("D365FO:staging"),
        // since the same branch name can mean different environments - or none - in different repositories.
        var active = new List<(string Id, string Name, string Project, DateTime Last)>();
        foreach (var p in projects)
        {
            try
            {
                var repos = await session.Client.GetListAsync($"{AdoClient.Scope(p)}_apis/git/repositories", ct);
                foreach (var repo in repos.Where(r => !Json.Bool(r, "isDisabled") && r.TryGetProperty("size", out var s) && s.GetInt64() > 0).Take(30))
                {
                    var id = Json.Str(repo, "id") ?? "";
                    var last = await session.Client.GetListAsync($"_apis/git/repositories/{id}/commits?searchCriteria.$top=1", ct);
                    if (last.Count > 0 && DateTime.TryParse(Json.Str(Json.Obj(last[0], "committer"), "date"), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal, out var when) && when > DateTime.UtcNow.AddDays(-183))
                        active.Add((id, Json.Str(repo, "name") ?? id, p, when));
                }
            }
            catch (AdoApiException) { } // no Git, or no Code (Read) scope
        }
        var several = active.Count > 1;
        foreach (var (id, name, inProject, last) in active)
        {
            try
            {
                var refs = await session.Client.GetListAsync($"_apis/git/repositories/{id}/refs?filter=heads/&$top=200", ct);
                foreach (var n in refs.Select(r => Code.Short(Json.Str(r, "name")) ?? "").Where(n => n.Length > 0 && !n.Contains('/')))
                {
                    var key = several ? $"{name}:{n}" : n;
                    keys.Add(key);
                    From(key, Ui.T($"Git: branch {n} of repository {name} ({inProject}; last commit in the repository {Day(last)})",
                                    $"Git: gałąź {n} repozytorium {name} ({inProject}; ostatni commit w repozytorium {Day(last)})"));
                }
            }
            catch (AdoApiException) { }
        }
        // TFVC branches ($/Project/Trunk/Main): named by the last part of their path, as ado_trace reads them. Only
        // branches with a changeset in the last half year; they come first, in their own flow order.
        var tfvc = new List<(string Key, string? Guess)>();
        foreach (var p in projects)
        {
            try
            {
                foreach (var b in Flatten(await session.Client.GetListAsync($"{AdoClient.Scope(p)}_apis/tfvc/branches?includeChildren=true&includeDeleted=false", ct)))
                {
                    var path = Json.Str(b, "path") ?? "";
                    var name = path[(path.LastIndexOf('/') + 1)..];
                    if (name.Length == 0) continue;
                    var last = await session.Client.GetListAsync($"_apis/tfvc/changesets?searchCriteria.itemPath={Uri.EscapeDataString(path)}&$top=1", ct);
                    if (last.Count == 0 || !DateTime.TryParse(Json.Str(last[0], "createdDate"), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal, out var when) || when < DateTime.UtcNow.AddDays(-183)) continue;
                    // Two branches with one name (Main in two projects) are one key: ado_trace knows a TFVC branch by its last part.
                    From(name, Ui.T($"TFVC: {path} (last changeset {Day(when)})", $"TFVC: {path} (ostatni changeset {Day(when)})"));
                    if (!tfvc.Any(t => t.Key.Equals(name, StringComparison.OrdinalIgnoreCase))) tfvc.Add((name, TfvcGuess(name)));
                }
            }
            catch (AdoApiException) { } // no TFVC, or no Code (Read) scope
        }

        // Release stages deployed in the last half year; a Git stage is named by its branch (with its repository when
        // there are several), a TFVC one as "Definition/Stage".
        foreach (var p in projects)
            foreach (var d in await session.Code.DeploymentsAsync(p, ct))
            {
                if (d.When is not { } when || when < DateTime.UtcNow.AddDays(-183)) continue;
                var branch = Code.Short(d.Branch);
                var repo = active.FirstOrDefault(a => string.Equals(a.Id, d.Repository, StringComparison.OrdinalIgnoreCase)).Name;
                var key = d.Branch?.StartsWith("$/", StringComparison.Ordinal) == true || branch is null ? $"{d.Definition}/{d.Stage}"
                    : several && repo is not null ? $"{repo}:{branch}" : branch;
                keys.Add(key);
                From(key, Ui.T($"release {d.Definition}, stage {d.Stage}: deployed {Day(d.When)}", $"release {d.Definition}, etap {d.Stage}: wdrożony {Day(d.When)}")
                          + (d.Branch is { } from ? Ui.T($" from {from}", $" z {from}") : "") + $" ({p})");
            }
        var git = TraceQueries.ProposeFlow(keys.Distinct(StringComparer.OrdinalIgnoreCase));
        var order = new[] { "DEV", "UAT", "PROD" };
        return tfvc.OrderBy(t => t.Guess is null ? 3 : Array.IndexOf(order, t.Guess)).ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .Concat(git.Where(g => !tfvc.Any(t => t.Key.Equals(g.Key, StringComparison.OrdinalIgnoreCase))))
            .Select(k => (k.Key, k.Guess, string.Join("; ", sources.GetValueOrDefault(k.Key) ?? [])))
            .DistinctBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A TFVC branch and the branches under it.</summary>
    static IEnumerable<JsonElement> Flatten(IEnumerable<JsonElement> branches)
    {
        foreach (var b in branches)
        {
            yield return b;
            foreach (var c in Flatten(Json.Arr(b, "children"))) yield return c;
        }
    }

    /// <summary>
    /// A first guess for a TFVC branch of a D365 project: Main (or Trunk, Dev) is where development goes in, Release is
    /// production; a test branch (SIT, UAT, Test) in between. Dated copies (SIT_20261021) and personal branches get none.
    /// </summary>
    internal static string? TfvcGuess(string name) => name.ToLowerInvariant() switch
    {
        "main" or "trunk" or "dev" or "develop" or "development" => "DEV",
        "sit" or "uat" or "test" or "qa" or "staging" or "preprod" => "UAT",
        "release" or "prod" or "production" or "live" => "PROD",
        _ => null,
    };
}

/// <summary>
/// The environment map read with its three kinds of keys: a release stage ("Definition/Stage"), a branch of every
/// repository ("staging"), or a branch of one repository ("D365FO:staging"). A repository with keys of its own uses only
/// those, so the same branch name can mean different things in different repositories (or nothing at all).
/// </summary>
public sealed class EnvMap(IReadOnlyDictionary<string, string> map)
{
    public IReadOnlyDictionary<string, string> Raw => map;
    public int Count => map.Count;
    readonly List<string> order = map.Keys.ToList();

    public static string? RepoOf(string key) => key.IndexOf(':') is var i and > 0 && !key.Contains('/') ? key[..i] : null;
    public static string BranchOf(string key) => key.IndexOf(':') is var i and > 0 && !key.Contains('/') ? key[(i + 1)..] : key;
    public static bool IsStage(string key) => key.Contains('/');

    bool HasOwn(string? repo) => repo is not null && map.Keys.Any(k => string.Equals(RepoOf(k), repo, StringComparison.OrdinalIgnoreCase));

    /// <summary>The key that names <paramref name="branch"/> of <paramref name="repo"/>, or null.</summary>
    public string? BranchKey(string? repo, string? branch)
    {
        if (string.IsNullOrEmpty(branch)) return null;
        if (HasOwn(repo)) return map.Keys.FirstOrDefault(k => string.Equals(RepoOf(k), repo, StringComparison.OrdinalIgnoreCase) && BranchOf(k).Equals(branch, StringComparison.OrdinalIgnoreCase));
        return map.Keys.FirstOrDefault(k => RepoOf(k) is null && !IsStage(k) && k.Equals(branch, StringComparison.OrdinalIgnoreCase));
    }

    public bool Has(string key) => map.ContainsKey(key);
    public string this[string key] => map[key];
    public string? Label(string? key) => key is not null && map.TryGetValue(key, out var v) ? v : null;

    /// <summary>Position in the flow (DEV first), or int.MaxValue.</summary>
    public int Order(string? key) => key is null ? int.MaxValue : order.FindIndex(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : int.MaxValue;

    /// <summary>The branches that mean an environment in <paramref name="repo"/>, in flow order.</summary>
    public IEnumerable<(string Branch, string Label)> BranchesOf(string? repo) =>
        (HasOwn(repo) ? map.Where(e => string.Equals(RepoOf(e.Key), repo, StringComparison.OrdinalIgnoreCase))
                      : map.Where(e => RepoOf(e.Key) is null && !IsStage(e.Key)))
        .Select(e => (BranchOf(e.Key), e.Value));

    /// <summary>Whether <paramref name="branch"/> is an environment branch of any repository (a promotion source).</summary>
    public bool IsEnvironmentBranch(string? repo, string branch) => BranchKey(repo, branch) is not null;
}
