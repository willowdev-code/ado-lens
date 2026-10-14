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
        // A task rarely has criteria of its own: what was asked is in its parent (story, bug).
        if (criteria.Length == 0 && Json.Arr(wi, "relations").FirstOrDefault(r => Json.Str(r, "rel") == "System.LinkTypes.Hierarchy-Reverse") is { ValueKind: JsonValueKind.Object } up
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
        changes = changes.OrderBy(c => c.Change.Date).ToList();
        var branches = new Dictionary<string, string?>();
        foreach (var cs in changes.Select(x => x.Change).Where(x => x.Kind == "changeset"))
            branches[cs.Id] = cs.Files.Count > 0 ? await code.TfvcBranchAsync(cs.Files[0].Path, ct) : null;

        sb.AppendLine("\nDONE");
        if (changes.Count == 0) sb.AppendLine("no changesets, commits or pull requests linked to the item or its tasks");
        foreach (var (c, from) in changes)
        {
            var via = from == id ? "" : $" (via #{from})";
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
        var env = session.Config.Environments;
        string Label(string? branch, string def, string stage)
        {
            var key = branch is null ? "" : branch.StartsWith("$/", StringComparison.Ordinal) ? branch[(branch.LastIndexOf('/') + 1)..] : Code.Short(branch)!;
            return env.TryGetValue(key, out var l) ? $"{l} ({def} / {stage})" : $"{def} / {stage}";
        }
        var where = new List<string>();
        var anyIn = false;
        foreach (var d in deployments.OrderBy(d => d.Definition).ThenBy(d => d.Stage))
        {
            bool? contains = null;
            if (d.Branch?.StartsWith("$/", StringComparison.Ordinal) == true)
            {
                var onBranch = changes.Where(c => c.Change.Kind == "changeset" && string.Equals(branches.GetValueOrDefault(c.Change.Id), d.Branch, StringComparison.OrdinalIgnoreCase))
                    .Select(c => int.Parse(c.Change.Id, CultureInfo.InvariantCulture)).ToList();
                if (onBranch.Count == 0) continue;
                contains = int.TryParse(d.SourceVersion, out var v) ? v >= onBranch.Max() : null;
            }
            else if (d.SourceVersion is { } deployedCommit && d.Repository is { } repo)
            {
                var gitChanges = changes.Select(x => x.Change)
                    .Where(x => x.Kind != "changeset" && string.Equals(x.RepoId, repo, StringComparison.OrdinalIgnoreCase)).ToList();
                if (gitChanges.Count == 0) continue;
                var results = new List<bool?>();
                // A PR not completed has not landed anywhere; everything else is checked against the deployed commit.
                foreach (var g in gitChanges)
                    results.Add(g.LandedCommit is { } commit ? await code.ContainsAsync(repo, commit, deployedCommit, false, ct) : false);
                contains = results.All(r => r == true) ? true : results.Any(r => r == false) ? false : null;
            }
            else continue;
            anyIn |= contains == true;
            where.Add($"- {Label(d.Branch, d.Definition, d.Stage)}: " + contains switch
            {
                true => $"YES - deployed {Date(d.When)} (build {d.BuildNumber})",
                false => $"NOT YET - last deployment {Date(d.When)} (build {d.BuildNumber}) does not contain it",
                _ => $"unknown - last deployment {Date(d.When)} (build {d.BuildNumber})",
            });
        }
        // Git branches named in the environment map, even without release pipelines: is the change merged there?
        foreach (var (branch, label) in env.Where(e => !e.Key.Contains('/')))
            foreach (var group in changes.Select(x => x.Change).Where(x => x.Kind != "changeset" && x.RepoId is not null).GroupBy(x => x.RepoId!))
            {
                var commits = group.Select(x => x.LandedCommit).ToList();
                var merged = !commits.Any(x => x is null)
                             && (await Task.WhenAll(commits.Select(x => code.ContainsAsync(group.Key, x!, branch, true, ct)))).All(r => r == true);
                where.Add($"- {label} branch {branch} ({group.First().Repo}): {(merged ? "merged" : "NOT merged yet")}");
            }
        if (where.Count > 0) sb.AppendLine("\nWHERE").AppendLine(string.Join('\n', where));
        else if (changes.Count > 0)
            sb.AppendLine("\nWHERE").AppendLine("no release stage deploys the branches of this change" +
                (env.Count == 0 ? $"; map branches to environments with 'adolens config --connection {session.Config.Connection} --env staging=UAT'" : ""));
        if (changes.Count > 0 && where.Count > 0 && !anyIn && !where.Any(w => w.Contains(": merged", StringComparison.Ordinal)))
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

        var sb = new StringBuilder();
        var head = c.Kind == "pr"
            ? $"{c.Label} {c.State}: {c.TargetBranch} <- {c.Branch}, \"{c.Message}\", {c.Author}"
            : $"{c.Label} {Date(c.Date)} {c.Author}: \"{Formatting.Collapse(c.Message ?? "", 200)}\"";
        sb.AppendLine(head);

        if (file is not null)
        {
            var matches = c.Files.Where(f => f.Path.EndsWith(file.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) matches = c.Files.Where(f => f.Path.Contains(file, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return sb.Append($"no changed file matches '{file}'; files: {string.Join(", ", c.Files.Select(f => f.Path[(f.Path.LastIndexOf('/') + 1)..]).Take(30))}").ToString();
            if (matches.Count > 1) return sb.Append($"'{file}' matches {matches.Count} files: {string.Join("; ", matches.Select(f => f.Path).Take(15))} - give more of the path").ToString();
            var f = matches[0];
            var (before, after) = await versions(f);
            sb.AppendLine($"{f.Path} ({f.ChangeType})");
            if (before is null && after is null) return sb.Append("content not readable (binary file or no access)").ToString();
            var max = detail == "full" ? 5000 : 400;
            sb.Append(AotDiff.IsAot(f.Path) ? AotDiff.Detail(before, after, max)
                : Diff.Unified(before ?? "", after ?? "", 3, max) ?? "no change in the text");
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
