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

    public static async Task<List<(string Key, string? Guess)>> DetectAsync(Session session, string project, CancellationToken ct)
    {
        var keys = new List<string>();
        foreach (var d in await session.Code.DeploymentsAsync(project, ct))
        {
            if (d.When is not { } when || when < DateTime.UtcNow.AddDays(-183)) continue;
            keys.Add(d.Branch?.StartsWith("$/", StringComparison.Ordinal) == true ? $"{d.Definition}/{d.Stage}" : Code.Short(d.Branch) ?? $"{d.Definition}/{d.Stage}");
        }
        // Long-lived branches of the repositories in use (a commit in the last half year); feature and work branches are
        // filtered by ProposeFlow. With several repositories each branch is named with its repository ("D365FO:staging"),
        // since the same branch name can mean different environments - or none - in different repositories.
        try
        {
            var repos = await session.Client.GetListAsync($"{AdoClient.Scope(project)}_apis/git/repositories", ct);
            var active = new List<(string Id, string Name)>();
            foreach (var repo in repos.Where(r => !Json.Bool(r, "isDisabled") && r.TryGetProperty("size", out var s) && s.GetInt64() > 0).Take(30))
            {
                var id = Json.Str(repo, "id") ?? "";
                var last = await session.Client.GetListAsync($"_apis/git/repositories/{id}/commits?searchCriteria.$top=1", ct);
                if (last.Count > 0 && DateTime.TryParse(Json.Str(Json.Obj(last[0], "committer"), "date"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var when) && when > DateTime.UtcNow.AddDays(-183))
                    active.Add((id, Json.Str(repo, "name") ?? id));
            }
            foreach (var (id, name) in active)
            {
                var refs = await session.Client.GetListAsync($"_apis/git/repositories/{id}/refs?filter=heads/&$top=200", ct);
                keys.AddRange(refs.Select(r => Code.Short(Json.Str(r, "name")) ?? "").Where(n => n.Length > 0 && !n.Contains('/'))
                    .Select(n => active.Count > 1 ? $"{name}:{n}" : n));
            }
        }
        catch (AdoApiException) { } // no Git, or no Code (Read) scope
        return TraceQueries.ProposeFlow(keys.Distinct(StringComparer.OrdinalIgnoreCase));
    }
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
