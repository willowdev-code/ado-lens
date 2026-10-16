using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>
/// "Did they do what was asked, and where is it?" — a work item's acceptance criteria next to the code linked to it
/// (and to its tasks), the builds, and every release stage with whether it already contains the change.
/// </summary>
public static partial class TraceQueries
{
    sealed record Link(string Kind, string Value, int From);

    [GeneratedRegex(@"^vstfs:///(?<kind>VersionControl/Changeset|Git/Commit|Git/PullRequestId|Build/Build|Git/Ref)/(?<value>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ArtifactUrl();

    static List<Link> Links(JsonElement wi)
    {
        var id = WorkItems.Id(wi);
        var list = new List<Link>();
        foreach (var r in Json.Arr(wi, "relations").Where(r => Json.Str(r, "rel") == "ArtifactLink"))
            if (ArtifactUrl().Match(Json.Str(r, "url") ?? "") is { Success: true } m)
                list.Add(new Link(m.Groups["kind"].Value.ToLowerInvariant(), Uri.UnescapeDataString(m.Groups["value"].Value), id));
        return list;
    }

    /// <summary>When an update was made: its System.ChangedDate (revisedDate of the latest update is 9999-01-01).</summary>
    static DateTime? UpdateTime(JsonElement update)
    {
        var changed = Json.Obj(Json.Obj(Json.Obj(update, "fields"), "System.ChangedDate"), "newValue");
        var text = Json.Scalar(changed) ?? Json.Str(update, "revisedDate");
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) && d.Year < 9999 ? d : null;
    }

    static string Date(DateTime? d) => d?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?";

    static IEnumerable<string> Objects(CodeChange c) =>
        c.Files.Select(f => AotDiff.IsAot(f.Path) ? AotDiff.ObjectName(f.Path) : f.Path[(f.Path.LastIndexOf('/') + 1)..])
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    public static async Task<string> TraceAsync(Lens lens, string? connection, int id, string? detail, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var work = session.Work;
        var code = session.Code;
        var full = detail == "full";
        var summary = detail == "summary";

        var items = await work.GetAsync([id], relations: true, ct: ct);
        if (items.Count == 0) return $"no work item #{id} visible with this PAT";
        var wi = items[0];
        var project = WorkItems.Field(wi, "System.TeamProject") ?? session.Project(null);
        var childIds = Json.Arr(wi, "relations").Where(r => Json.Str(r, "rel") == "System.LinkTypes.Hierarchy-Forward")
            .Select(r => Json.Str(r, "url") ?? "").Select(u => int.TryParse(u[(u.LastIndexOf('/') + 1)..], out var c) ? c : 0).Where(c => c > 0).ToList();
        var children = childIds.Count == 0 ? [] : await work.GetAsync(childIds, relations: true, ct: ct);
        var s = (await session.Meta.GetAsync(project, ct)).Snapshot;
        string Category(JsonElement x) =>
            s.FindType(WorkItems.Field(x, "System.WorkItemType") ?? "")?.States.FirstOrDefault(st => st.Name == WorkItems.Field(x, "System.State"))?.Category ?? "";

        var sb = new StringBuilder();
        sb.AppendLine(WorkItemFormat.Header(wi));
        sb.AppendLine(WorkItemFormat.Url(work.WebBase, wi));
        var signals = new List<string>();

        // 1. What was asked.
        var criteria = Html.ToMarkdown(WorkItems.Field(wi, WorkItems.AcceptanceRef));
        var description = Html.ToMarkdown(WorkItems.Field(wi, WorkItems.DescriptionRef) ?? WorkItems.Field(wi, WorkItems.ReproStepsRef));
        sb.AppendLine("\nASKED");
        // A task rarely has criteria of its own: what was asked is in its parent (story, bug). A requirement's parent
        // (a feature or epic) is broader than the requirement, so it is used only when the item says nothing at all.
        var isTask = s.FindType(WorkItems.Field(wi, "System.WorkItemType") ?? "")?.Level == Levels.Task;
        if (criteria.Length == 0 && (isTask || description.Length == 0) && Json.Arr(wi, "relations").FirstOrDefault(r => Json.Str(r, "rel") == "System.LinkTypes.Hierarchy-Reverse") is { ValueKind: JsonValueKind.Object } up
            && int.TryParse((Json.Str(up, "url") ?? "")[((Json.Str(up, "url") ?? "").LastIndexOf('/') + 1)..], out var parentId)
            && (await work.GetAsync([parentId], relations: false, ct: ct)).FirstOrDefault() is { ValueKind: JsonValueKind.Object } parent)
        {
            var parentCriteria = Html.ToMarkdown(WorkItems.Field(parent, WorkItems.AcceptanceRef));
            sb.AppendLine($"from the parent {WorkItemFormat.Header(parent)}:");
            if (parentCriteria.Length > 0) criteria = parentCriteria;
            else if (description.Length == 0) description = Html.ToMarkdown(WorkItems.Field(parent, WorkItems.DescriptionRef) ?? WorkItems.Field(parent, WorkItems.ReproStepsRef));
        }
        if (criteria.Length > 0) sb.AppendLine("acceptance criteria:").AppendLine(criteria.Length > 2500 && !full ? criteria[..2500] + "…" : criteria);
        else
        {
            sb.AppendLine("acceptance criteria: none in their field" + (description.Length > 0 ? "; description:" : ""));
            if (description.Length > 0) sb.AppendLine(description.Length > 1500 && !full ? description[..1500] + "…" : description);
            signals.Add(description.Length > 0 ? "no acceptance criteria: the check can only go by the description"
                : "no acceptance criteria and no description: nothing to check the code against (look at the parent item)");
        }
        var criteriaChanges = (await work.UpdatesAsync(project, id, ct))
            .Where(u => Json.Obj(Json.Obj(u, "fields"), WorkItems.AcceptanceRef).ValueKind == JsonValueKind.Object)
            .Select(u => (When: UpdateTime(u), Who: Json.Str(Json.Obj(u, "revisedBy"), "displayName")))
            .Where(x => x.When is not null).ToList();

        // 2. What was done: code linked to the item and to its tasks.
        var links = Links(wi).Concat(children.SelectMany(Links)).DistinctBy(l => (l.Kind, l.Value)).ToList();
        var changes = new List<(CodeChange Change, int From)>();
        var failed = new List<string>();
        await Parallel.ForEachAsync(links.Where(l => l.Kind != "build/build" && l.Kind != "git/ref"),
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (l, token) =>
            {
                try
                {
                    var parts = l.Value.Split('/');
                    CodeChange c = l.Kind switch
                    {
                        "versioncontrol/changeset" => await code.ChangesetAsync(int.Parse(parts[^1], CultureInfo.InvariantCulture), token),
                        "git/commit" => await code.CommitAsync(parts[1], parts[2], token),
                        _ => await code.PullRequestAsync(parts[1], int.Parse(parts[2], CultureInfo.InvariantCulture), withFiles: false, token),
                    };
                    lock (changes) changes.Add((c, l.From));
                }
                catch (AdoApiException ex) { lock (failed) failed.Add($"{l.Kind.Split('/')[^1]} {l.Value.Split('/')[^1]}: {Formatting.Collapse(ex.Message, 100)}"); }
            });
        changes = Combine(changes.OrderBy(c => c.Change.Date).ToList());
        var (env, envSource) = await EnvironmentFlow.EffectiveAsync(session, project, ct);
        // A PR from a long-lived branch (a release branch into master, staging into master…) carries many items at once:
        // it is shown in one line and counts as evidence of where the work is, not as work of its own.
        var prTargets = changes.Where(x => x.Change.Kind == "pr").Select(x => x.Change.TargetBranch).Where(t => t is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsPromotion(CodeChange x) =>
            x.Kind == "pr" && x.Branch is { } src && (env.ContainsKey(src) || prTargets.Contains(src) || x.Files.Count > 100);
        var branches = new Dictionary<string, string?>();
        foreach (var cs in changes.Select(x => x.Change).Where(x => x.Kind == "changeset"))
            branches[cs.Id] = cs.Files.Count > 0 ? await code.TfvcBranchAsync(cs.Files[0].Path, ct) : null;

        sb.AppendLine("\nDONE");
        if (changes.Count == 0) sb.AppendLine("no changesets, commits or pull requests linked to the item or its tasks");
        var seenFiles = new Dictionary<string, string>();
        foreach (var (c, from) in changes)
        {
            var via = from == id ? "" : $" (via #{from})";
            if (IsPromotion(c))
            {
                sb.AppendLine($"- {c.Label} {c.State}{(c.State == "completed" ? $" {Date(c.Date)}" : "")}: {c.TargetBranch} <- {c.Branch} " +
                              $"(merge of branch {c.Branch}{(c.Files.Count > 0 ? $", {c.Files.Count} files" : "")}){via}");
                continue;
            }
            var line = c.Kind switch
            {
                "pr" => $"{c.Label} {c.State}{(c.State == "completed" ? $" {Date(c.Date)}" : "")}: {c.TargetBranch} <- {c.Branch}, \"{Formatting.Collapse(c.Message ?? "", 80)}\", {c.Author}",
                _ => $"{c.Label} {Date(c.Date)} {c.Author}: \"{Formatting.Collapse(c.Message ?? "", 80)}\"" +
                     (c.Kind == "changeset" && branches.GetValueOrDefault(c.Id) is { } b ? $" on {b}" : ""),
            };
            sb.AppendLine("- " + line + via);
            if (!summary && c.Files.Count > 0)
            {
                var objects = Objects(c).ToList();
                // The same branch is often merged into dev, staging and a release branch: say so instead of repeating.
                var key = string.Join('|', objects.Order(StringComparer.OrdinalIgnoreCase));
                if (seenFiles.TryGetValue(key, out var earlier)) { sb.AppendLine($"  same {c.Files.Count} file(s) as {earlier}"); continue; }
                seenFiles[key] = c.Label.Split(" (")[0];
                var shown = full ? objects : objects.Take(12).ToList();
                sb.AppendLine($"  {c.Files.Count} file(s): {string.Join(", ", shown)}{(objects.Count > shown.Count ? $", … {objects.Count - shown.Count} more" : "")}");
            }
        }
        foreach (var f in failed) sb.AppendLine($"- not readable: {f}");
        if (!summary && changes.Count > 0) sb.AppendLine("ado_changes change=<changeset|commit|PR> shows what changed (X++ per method).");

        var firstCode = changes.Select(c => c.Change.Date).Where(d => d is not null).Min();
        if (firstCode is { } fc && criteriaChanges.Where(x => x.When > fc).ToList() is { Count: > 0 } late)
            signals.Add($"acceptance criteria changed after the first code change ({string.Join(", ", late.Select(x => $"{Date(x.When)} {x.Who}"))}) - check the code against the current version");
        if (changes.Count == 0)
            signals.Add(Category(wi) == "Completed" ? "closed without any linked code" : "no code linked yet");
        foreach (var pr in changes.Select(x => x.Change).Where(x => x.Kind == "pr" && x.State is "active" or "abandoned"))
            signals.Add($"{pr.Label} is {pr.State}");
        var openTasks = children.Where(c => Category(c) is not ("Completed" or "Removed")).ToList();
        if (Category(wi) == "Completed" && openTasks.Count > 0)
            signals.Add($"closed with {openTasks.Count} open task(s): {string.Join(", ", openTasks.Select(t => "#" + WorkItems.Id(t)))}");

        // 3. Builds that integrated the change (old ones are deleted by retention).
        var buildIds = links.Where(l => l.Kind == "build/build").Select(l => int.TryParse(l.Value, out var b) ? b : 0).Where(b => b > 0)
            .Distinct().OrderByDescending(b => b).ToList();
        if (buildIds.Count > 0)
        {
            sb.AppendLine("\nBUILDS");
            var shownBuilds = buildIds.Take(full ? 50 : 5).ToList();
            var builds = await Task.WhenAll(shownBuilds.Select(b => code.BuildAsync(project, b, ct)));
            for (var i = 0; i < shownBuilds.Count; i++)
            {
                if (builds[i] is not { } b) { sb.AppendLine($"- build {shownBuilds[i]}: no longer kept (deleted by retention)"); continue; }
                sb.AppendLine($"- {Json.Str(Json.Obj(b, "definition"), "name")} {Json.Str(b, "buildNumber")}: {Json.Str(b, "result") ?? Json.Str(b, "status")}, " +
                              $"{Code.Short(Json.Str(b, "sourceBranch"))} @ {Json.Str(b, "sourceVersion")?[..Math.Min(8, Json.Str(b, "sourceVersion")!.Length)]}, {Date(DateTime.TryParse(Json.Str(b, "finishTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var f) ? f : null)}");
                if (i == 0 && Json.Str(b, "result") is "failed" or "partiallySucceeded") signals.Add($"the latest linked build ({Json.Str(b, "buildNumber")}) {Json.Str(b, "result")}");
            }
            if (buildIds.Count > shownBuilds.Count) sb.AppendLine($"- … {buildIds.Count - shownBuilds.Count} older build(s)");
        }

        // 4. Where it is: every release stage whose source is a branch the change is on.
        // Releases live with the code, which can be in another project than the work item.
        var projects = changes.Select(x => x.Change.Project).Append(project).Where(p => p is not null).Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var deployments = (await Task.WhenAll(projects.Select(p => code.DeploymentsAsync(p, ct)))).SelectMany(x => x).ToList();
        // The environment map names a release stage ("Definition/Stage", e.g. one stage deploying to UAT and SCM at once),
        // a whole release definition, or a branch ("staging"; TFVC: the last part of the branch path, e.g. "Main").
        var envOrder = env.Keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.OrdinalIgnoreCase);
        string? EnvKey(string? branch, string def, string stage)
        {
            var branchKey = branch is null ? null : branch.StartsWith("$/", StringComparison.Ordinal) ? branch[(branch.LastIndexOf('/') + 1)..] : Code.Short(branch);
            return new[] { $"{def}/{stage}", def, branchKey }.FirstOrDefault(k => k is not null && env.ContainsKey(k));
        }
        string Label(string? branch, string def, string stage) =>
            EnvKey(branch, def, stage) is { } k ? $"{env[k]} ({def} / {stage})" : $"{def} / {stage}";
        int Order(string? branch, string def, string stage) => EnvKey(branch, def, stage) is { } k ? envOrder[k] : int.MaxValue;
        var where = new List<string>();
        var anyIn = false;
        // Git: a stage has the work when any of its changes (a PR merge, a commit) is in what was deployed. Changes that
        // are not there yet are named, unless a later change from the same source branch already is (it carries them).
        // Each answer is one Azure DevOps call: asked once, all at the same time (a sequential walk timed out on items
        // with several PRs and many release stages).
        var containsCache = new System.Collections.Concurrent.ConcurrentDictionary<(string, string, bool), Task<bool?>>();
        Task<bool?> Contains(string repo, string commit, string target, bool isBranch) =>
            containsCache.GetOrAdd((commit, target, isBranch), _ => code.ContainsAsync(repo, commit, target, isBranch, ct));
        async Task<(List<CodeChange> In, List<CodeChange> NotYet, bool Known)> GitStatus(string repo, string target, bool isBranch, DateTime? deployedAt)
        {
            var git = changes.Select(x => x.Change).Where(x => x.Kind != "changeset" && string.Equals(x.RepoId, repo, StringComparison.OrdinalIgnoreCase)).ToList();
            // A deployment that finished before a change was merged cannot contain it: no need to ask.
            var answers = await Task.WhenAll(git.Select(async g =>
                g.LandedCommit is not { } commit ? (bool?)null
                : deployedAt is { } at && g.Date is { } merged && at < merged ? false
                : await Contains(repo, commit, target, isBranch)));
            var inside = git.Where((g, i) => answers[i] == true).ToList();
            var known = answers.Any(a => a is not null);
            var notYet = git.Where(g => !inside.Contains(g) && !IsPromotion(g)
                && !inside.Any(i => i.Branch is not null && i.Branch == g.Branch && i.Date >= g.Date)).ToList();
            return (inside, notYet, known);
        }
        static string Names(IEnumerable<CodeChange> list) =>
            string.Join(", ", list.Select(x => x.Kind == "pr" ? $"PR {x.Id} ({x.TargetBranch}, {x.Date?.ToLocalTime():MM-dd})" : $"commit {x.Id[..Math.Min(7, x.Id.Length)]}"));

        var stageTasks = new List<Task<string?>>();
        foreach (var d in deployments.OrderBy(d => Order(d.Branch, d.Definition, d.Stage)).ThenBy(d => d.Definition).ThenBy(d => d.Stage))
        {
            // With an environment map, a stage outside it that has not deployed for half a year is an old pipeline.
            if (env.Count > 0 && EnvKey(d.Branch, d.Definition, d.Stage) is null && (d.When is not { } last || last < DateTime.UtcNow.AddDays(-183)))
                continue;
            var label = $"- {Label(d.Branch, d.Definition, d.Stage)}: ";
            var deployed = $"{Date(d.When)} (build {d.BuildNumber})";
            if (d.Branch?.StartsWith("$/", StringComparison.Ordinal) == true)
            {
                var onBranch = changes.Where(c => c.Change.Kind == "changeset" && string.Equals(branches.GetValueOrDefault(c.Change.Id), d.Branch, StringComparison.OrdinalIgnoreCase))
                    .Select(c => int.Parse(c.Change.Id, CultureInfo.InvariantCulture)).ToList();
                if (onBranch.Count == 0) continue;
                bool? contains = int.TryParse(d.SourceVersion, out var v) ? v >= onBranch.Max() : null;
                anyIn |= contains == true;
                stageTasks.Add(Task.FromResult<string?>(label + contains switch
                {
                    true => $"YES - deployed {deployed}",
                    false => $"NOT YET - last deployment {deployed} does not contain it",
                    _ => $"unknown - last deployment {deployed}",
                }));
            }
            else if (d.SourceVersion is { } deployedCommit && d.Repository is { } repo
                     && changes.Any(x => x.Change.Kind != "changeset" && string.Equals(x.Change.RepoId, repo, StringComparison.OrdinalIgnoreCase)))
            {
                stageTasks.Add(Task.Run(async () =>
                {
                    var (inside, notYet, _) = await GitStatus(repo, deployedCommit, false, d.When);
                    if (inside.Count > 0) anyIn = true;
                    return (string?)(label + (inside.Count > 0
                        ? $"YES - deployed {deployed}: {Names(inside.Where(x => !IsPromotion(x)).DefaultIfEmpty(inside[0]))}" + (notYet.Count > 0 ? $"; not yet: {Names(notYet)}" : "")
                        : $"NOT YET - last deployment {deployed} has none of it"));
                }, ct));
            }
        }
        // Git branches named in the environment map, even without release pipelines: is the work merged there?
        // Keys that are no branch of the repository (a release definition, a TFVC branch) give no answer and are skipped.
        foreach (var (branch, envLabel) in env.Where(e => !e.Key.Contains('/')))
            foreach (var group in changes.Select(x => x.Change).Where(x => x.Kind != "changeset" && x.RepoId is not null).GroupBy(x => x.RepoId!))
            {
                var repoName = group.First().Repo;
                stageTasks.Add(Task.Run(async () =>
                {
                    var (inside, notYet, known) = await GitStatus(group.Key, branch, true, null);
                    if (!known) return null;
                    if (inside.Count > 0) anyIn = true;
                    return (string?)($"- {envLabel} = branch {branch} ({repoName}): " + (inside.Count > 0
                        ? $"merged: {Names(inside.Where(x => !IsPromotion(x)).DefaultIfEmpty(inside[0]))}" + (notYet.Count > 0 ? $"; not yet: {Names(notYet)}" : "")
                        : "NOT merged yet"));
                }, ct));
            }
        where.AddRange((await Task.WhenAll(stageTasks)).Where(l => l is not null).Select(l => l!));
        if (where.Count > 0) sb.AppendLine("\nWHERE").AppendLine(string.Join('\n', where));
        else if (changes.Count > 0) sb.AppendLine("\nWHERE").AppendLine("no release stage deploys the branches of this change");

        // No environment names yet: propose the flow found here, so Claude asks once and saves it for good.
        if (env.Count == 0 && changes.Count > 0)
        {
            // Stages not deployed for half a year are leftovers, not part of the flow.
            var candidates = deployments
                .Where(d => d.When is { } w && w > DateTime.UtcNow.AddDays(-183))
                .Where(d => where.Any(w => w.Contains($"{d.Definition} / {d.Stage}", StringComparison.Ordinal)))
                .Select(d => d.Branch?.StartsWith("$/", StringComparison.Ordinal) == true ? $"{d.Definition}/{d.Stage}" : Code.Short(d.Branch))
                .Concat(changes.Select(x => x.Change.TargetBranch))
                .Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var proposal = ProposeFlow(candidates);
            if (proposal.Count > 0)
                sb.AppendLine("\nSETUP").AppendLine(
                    $"environment names are not set for connection {session.Config.Connection}. Found: {string.Join(", ", proposal.Select(p => p.Guess is null ? p.Key : $"{p.Key} (looks like {p.Guess})"))}. " +
                    "Ask the user what each is called and in which order code moves through them (e.g. DEV sbx01 -> UAT sbx03 -> PROD), " +
                    "save it once with ado_settings environments=\"" + string.Join("; ", proposal.Select(p => $"{p.Key}={p.Guess ?? "<name>"}")) + "\" " +
                    "(scope=team puts it on the wiki page for the whole team, after the user agrees), then answer with those names.");
        }
        if (changes.Count > 0 && where.Count > 0 && !anyIn)
            signals.Add("not deployed to any stage yet");

        // 5. Tests linked to the requirement.
        var tests = Json.Arr(wi, "relations").Where(r => Json.Str(r, "rel") == "Microsoft.VSTS.Common.TestedBy-Forward")
            .Select(r => Json.Str(r, "url") ?? "").Select(u => int.TryParse(u[(u.LastIndexOf('/') + 1)..], out var t) ? t : 0).Where(t => t > 0).ToList();
        if (tests.Count > 0)
        {
            var testItems = await work.GetAsync(tests, relations: false, ["System.Id", "System.WorkItemType", "System.State", "System.Title", "System.TeamProject"], ct);
            sb.AppendLine("\nTESTS").AppendLine(string.Join('\n', testItems.Select(t => "- " + WorkItemFormat.Header(t))));
        }
        if (children.Count > 0 && !summary)
            sb.AppendLine("\nTASKS").AppendLine(string.Join('\n', children.Select(t => $"- {WorkItemFormat.Header(t)}{(WorkItems.Field(t, "System.AssignedTo") is { } who ? $" ({who})" : "")}")));

        sb.AppendLine("\nSIGNALS").AppendLine(signals.Count == 0 ? "- none" : string.Join('\n', signals.Select(x => "- " + x)));
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A first guess of the flow from branch and stage names: development branches, then test / staging, then
    /// production; release branches (R350, release/2.1) are steps on the way, not environments.
    /// </summary>
    internal static List<(string Key, string? Guess)> ProposeFlow(IEnumerable<string> keys)
    {
        static (int Order, string? Guess) Classify(string key)
        {
            var k = key.ToLowerInvariant();
            var last = k[(k.LastIndexOf('/') + 1)..];
            if (Regex.IsMatch(k, @"^(r\d+|release.*|hotfix.*|feature.*|bugfix.*|users/.*)$")) return (-1, null);
            if (last is "dev" or "develop" or "development" or "sit" or "int" or "integration") return (0, "DEV");
            if (last is "staging" or "stage" or "uat" or "test" or "qa" or "preprod") return (1, "UAT");
            if (last is "master" or "main" or "prod" or "production") return (2, "PROD");
            return (3, null);
        }
        return keys.Select(k => (Key: k, C: Classify(k))).Where(x => x.C.Order >= 0)
            .OrderBy(x => x.C.Order).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Key, x.C.Guess)).ToList();
    }

    [GeneratedRegex(@"^Merged PR (?<n>\d+)")]
    private static partial Regex MergedPr();

    /// <summary>A completed PR and its merge commit are one change: the commit is dropped and its files go to the PR.</summary>
    internal static List<(CodeChange Change, int From)> Combine(List<(CodeChange Change, int From)> changes)
    {
        var prs = changes.Select(x => x.Change).Where(x => x.Kind == "pr").ToList();
        CodeChange? PrOf(CodeChange commit) =>
            prs.FirstOrDefault(p => string.Equals(p.MergeCommit, commit.Id, StringComparison.OrdinalIgnoreCase))
            ?? (MergedPr().Match(commit.Message ?? "") is { Success: true } m ? prs.FirstOrDefault(p => p.Id == m.Groups["n"].Value) : null);
        var result = new List<(CodeChange, int)>();
        foreach (var (c, from) in changes)
        {
            if (c.Kind == "commit" && PrOf(c) is not null) continue;
            var commit = c.Kind == "pr" && c.Files.Count == 0
                ? changes.Select(x => x.Change).FirstOrDefault(x => x.Kind == "commit" && ReferenceEquals(PrOf(x), c))
                : null;
            result.Add((commit is null ? c : c with { Files = commit.Files }, from));
        }
        return result;
    }

    [GeneratedRegex(@"^(?:c|cs|changeset)?\s*#?(?<n>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChangesetRef();

    [GeneratedRegex(@"^(?:pr|pull\s*request|!)\s*#?(?<n>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PullRequestRef();

    [GeneratedRegex(@"^(?:commit\s+)?(?<sha>[0-9a-f]{7,40})$", RegexOptions.IgnoreCase)]
    private static partial Regex CommitRef();

    /// <summary>A changeset ("523", "C523"), pull request ("PR 482", "!482") or commit (sha, with repo): files, and diffs.</summary>
    public static async Task<string> ChangesAsync(Lens lens, string? connection, string change, string? repo, string? file, string? detail, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var (c, versions) = await ResolveAsync(session, change, repo, ct);
        return await ChangesTextAsync(session, c, versions, file, detail);
    }

    /// <summary>A changeset, pull request or commit, and how to read the two versions of each of its files.</summary>
    internal static async Task<(CodeChange Change, Func<FileChange, Task<(string? Before, string? After)>> Versions)> ResolveAsync(
        Session session, string change, string? repo, CancellationToken ct)
    {
        var code = session.Code;
        var text = change.Trim();
        CodeChange c;
        Func<FileChange, Task<(string? Before, string? After)>> versions;
        if (PullRequestRef().Match(text) is { Success: true } pm)
        {
            c = await code.PullRequestAsync(repo is null ? null : (await code.RepoAsync(repo, session.Config.Project, ct)).Id, int.Parse(pm.Groups["n"].Value, CultureInfo.InvariantCulture), withFiles: true, ct);
            versions = async f => (f.ChangeType.Contains("add") ? null : await code.GitFileAsync(c.RepoId!, f.OldPath ?? f.Path, c.BaseCommit, ct),
                                   f.ChangeType.Contains("delete") ? null : await code.GitFileAsync(c.RepoId!, f.Path, c.HeadCommit, ct));
        }
        else if (ChangesetRef().Match(text) is { Success: true } cm && !(text.Length >= 7 && CommitRef().IsMatch(text) && text.Any(char.IsLetter)))
        {
            var n = int.Parse(cm.Groups["n"].Value, CultureInfo.InvariantCulture);
            c = await code.ChangesetAsync(n, ct);
            versions = async f => (f.ChangeType.Contains("add") && !f.ChangeType.Contains("merge") ? null : await code.TfvcFileAsync(f.OldPath ?? f.Path, n - 1, ct),
                                   f.ChangeType.Contains("delete") ? null : await code.TfvcFileAsync(f.Path, n, ct));
        }
        else if (CommitRef().Match(text) is { Success: true } gm)
        {
            if (repo is null) throw new ArgumentException("a commit needs repo=<repository name> (ado_trace shows it next to the commit)");
            var r = await code.RepoAsync(repo, session.Config.Project, ct);
            c = await code.CommitAsync(r.Id, gm.Groups["sha"].Value, ct);
            versions = async f => (f.ChangeType.Contains("add") ? null : await code.GitFileAsync(r.Id, f.OldPath ?? f.Path, c.Parent, ct),
                                   f.ChangeType.Contains("delete") ? null : await code.GitFileAsync(r.Id, f.Path, c.Id, ct));
        }
        else throw new ArgumentException("change is a changeset (523, C523), a pull request (PR 482) or a commit sha with repo");
        return (c, versions);
    }

    /// <summary>The work items linked to a change, with type, state and tags (a commit: the ids in its message).</summary>
    internal static async Task<string?> WorkItemsOfAsync(Session session, CodeChange c, CancellationToken ct)
    {
        List<int> ids;
        try
        {
            ids = c.Kind switch
            {
                "changeset" => (await session.Client.GetListAsync($"_apis/tfvc/changesets/{c.Id}/workItems", ct)).Select(w => w.TryGetProperty("id", out var i) ? i.GetInt32() : 0).ToList(),
                "pr" => (await session.Client.GetListAsync($"_apis/git/repositories/{c.RepoId}/pullRequests/{c.Id}/workitems", ct)).Select(w => int.TryParse(Json.Str(w, "id"), out var i) ? i : 0).ToList(),
                _ => System.Text.RegularExpressions.Regex.Matches(c.Message ?? "", @"#(\d{2,7})\b").Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList(),
            };
        }
        catch (AdoApiException) { return null; }
        ids = ids.Where(i => i > 0).Distinct().ToList();
        if (ids.Count == 0) return "work items: none linked";
        var items = await session.Work.GetAsync(ids, relations: false, ["System.Id", "System.WorkItemType", "System.State", "System.Title", "System.Tags"], ct);
        return "work items: " + string.Join("; ", items.Select(w => $"#{WorkItems.Id(w)} {WorkItems.Field(w, "System.WorkItemType")} {Formatting.Collapse(WorkItems.Field(w, "System.Title") ?? "", 50)} ({WorkItems.Field(w, "System.State")})" +
            (WorkItems.Field(w, "System.Tags") is { Length: > 0 } t ? $" [tags: {t}]" : "")));
    }

    static async Task<string> ChangesTextAsync(Session session, CodeChange c, Func<FileChange, Task<(string? Before, string? After)>> versions, string? file, string? detail)
    {
        var ct = CancellationToken.None;
        var sb = new StringBuilder();
        var head = c.Kind == "pr"
            ? $"{c.Label} {c.State}: {c.TargetBranch} <- {c.Branch}, \"{c.Message}\", {c.Author}"
            : $"{c.Label} {Date(c.Date)} {c.Author}: \"{Formatting.Collapse(c.Message ?? "", 200)}\"";
        sb.AppendLine(head);
        if (file is null && await WorkItemsOfAsync(session, c, ct) is { } linked) sb.AppendLine(linked);

        if (file is not null)
        {
            // Several files at once ("a;b;c"): one call instead of one per file, each diff a little shorter.
            var names = file.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var max = detail == "full" ? 5000 : Math.Max(150, 400 / Math.Max(1, names.Length) * 2);
            var picked = new List<FileChange>();
            foreach (var name in names)
            {
                var matches = c.Files.Where(f => f.Path.EndsWith(name.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0) matches = c.Files.Where(f => f.Path.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0) sb.AppendLine($"no changed file matches '{name}'; files: {string.Join(", ", c.Files.Select(f => f.Path[(f.Path.LastIndexOf('/') + 1)..]).Take(30))}");
                else if (matches.Count > 1) sb.AppendLine($"'{name}' matches {matches.Count} files: {string.Join("; ", matches.Select(f => f.Path).Take(15))} - give more of the path");
                else if (!picked.Contains(matches[0])) picked.Add(matches[0]);
            }
            var diffs = await Task.WhenAll(picked.Select(async f =>
            {
                var (before, after) = await versions(f);
                var body = before is null && after is null ? "content not readable (binary file or no access)"
                    : AotDiff.IsAot(f.Path) ? AotDiff.Detail(before, after, max)
                    : Diff.Unified(before ?? "", after ?? "", 3, max) ?? "no change in the text";
                return $"{f.Path} ({f.ChangeType})\n{body.TrimEnd()}";
            }));
            sb.AppendJoin("\n\n", diffs);
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"{c.Files.Count} file(s):");
        var list = detail == "full" ? c.Files : c.Files.Take(40).ToList();
        // A one-line summary per X++ object: which methods were added, changed or removed.
        var summaries = new Dictionary<string, string>();
        if (detail != "summary")
            await Parallel.ForEachAsync(list.Where(f => AotDiff.IsAot(f.Path)).Take(20), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
                async (f, _) =>
                {
                    var (before, after) = await versions(f);
                    lock (summaries) summaries[f.Path] = AotDiff.Summary(before, after);
                });
        foreach (var f in list)
            sb.AppendLine($"- {(AotDiff.IsAot(f.Path) ? AotDiff.ObjectName(f.Path) : f.Path[(f.Path.LastIndexOf('/') + 1)..])} [{f.ChangeType}]" +
                          (summaries.TryGetValue(f.Path, out var sum) ? $": {sum}" : ""));
        if (list.Count < c.Files.Count) sb.AppendLine($"- … {c.Files.Count - list.Count} more (detail=full)");
        sb.Append("file=<name> shows the diff of one file (X++ per method).");
        return sb.ToString();
    }
}
