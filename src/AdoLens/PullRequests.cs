using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>Pull requests: lists, one PR with reviewers, policies, work items and human comment threads, and actions.</summary>
public static partial class PullRequestQueries
{
    public static readonly string[] Actions = ["create", "comment", "reply", "resolve", "vote", "publish", "complete", "abandon"];

    static readonly Dictionary<string, int> Votes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["approve"] = 10, ["suggestions"] = 5, ["reset"] = 0, ["wait"] = -5, ["reject"] = -10,
    };

    static string VoteText(int vote) => vote switch
    {
        10 => "approved", 5 => "approved with suggestions", -5 => "waiting for author", -10 => "rejected", _ => "no vote",
    };

    static string Ago(string? iso)
    {
        if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d)) return "";
        var span = DateTime.UtcNow - d;
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d" : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h" : $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    static string Line(JsonElement pr)
    {
        var votes = Json.Arr(pr, "reviewers").GroupBy(r => r.TryGetProperty("vote", out var v) ? v.GetInt32() : 0)
            .OrderByDescending(g => g.Key).Select(g => $"{g.Count()} {VoteText(g.Key)}");
        var flags = new List<string>();
        if (Json.Bool(pr, "isDraft")) flags.Add("draft");
        if (Json.Str(pr, "mergeStatus") is "conflicts" or "failure") flags.Add(Json.Str(pr, "mergeStatus")!);
        return $"PR {pr.GetProperty("pullRequestId").GetInt32()} [{Json.Str(Json.Obj(pr, "repository"), "name")}] {Json.Str(pr, "title")}: " +
               $"{Code.Short(Json.Str(pr, "targetRefName"))} <- {Code.Short(Json.Str(pr, "sourceRefName"))} " +
               $"({Json.Str(Json.Obj(pr, "createdBy"), "displayName")}, {Ago(Json.Str(pr, "creationDate"))})" +
               (votes.Any() ? $"; {string.Join(", ", votes)}" : "") + (flags.Count > 0 ? $"; {string.Join(", ", flags)}" : "");
    }

    public static async Task<string> ListAsync(Lens lens, string? connection, string? project, string? list, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var (me, meName) = await new Boards(session).MeAsync(ct);
        var all = await session.Client.GetListAsync($"{AdoClient.Scope(name)}_apis/git/pullrequests?searchCriteria.status=active&$top=200", ct);
        var which = (list ?? "review").Trim().ToLowerInvariant();
        var review = all.Where(pr => Json.Arr(pr, "reviewers").Any(r => Json.Str(r, "id") == me && (!r.TryGetProperty("vote", out var v) || v.GetInt32() == 0)) && !Json.Bool(pr, "isDraft")).ToList();
        var mine = all.Where(pr => Json.Str(Json.Obj(pr, "createdBy"), "id") == me).ToList();
        var sb = new StringBuilder($"{meName} - active pull requests in {name}:\n");
        void Section(string title, List<JsonElement> prs, int max)
        {
            sb.AppendLine($"{title} ({prs.Count}){(prs.Count == 0 ? ": none" : ":")}");
            foreach (var pr in prs.Take(max)) sb.AppendLine("  " + Line(pr));
            if (prs.Count > max) sb.AppendLine($"  … {prs.Count - max} more");
        }
        Section("waiting for your review", review, 20);
        Section("yours", mine, 20);
        if (which == "active") Section("all active", all.Except(review).Except(mine).ToList(), 40);
        else sb.Append($"{all.Count} active in total; list=active shows all.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Pull requests completed into a branch (all repositories of the project, or one), newest first, optionally since a
    /// date, each with its work items: "what went into staging this week".
    /// </summary>
    public static async Task<string> MergedAsync(Lens lens, string? connection, string? project, string branch, string? repo, string? since, int top,
        CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var target = branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch.Trim();
        var from = since is null ? (DateTime?)null : Formatting.ParseSince(since);
        top = Math.Clamp(top, 1, 200);
        var repoFilter = repo is null ? "" : $"&searchCriteria.repositoryId={(await session.Code.RepoAsync(repo, name, ct)).Id}";
        // The API returns completed PRs newest first; read enough to cover the period, then cut.
        var list = await session.Client.GetListAsync(
            $"{AdoClient.Scope(name)}_apis/git/pullrequests?searchCriteria.status=completed&searchCriteria.targetRefName={Uri.EscapeDataString(target)}{repoFilter}&$top={(from is null ? top : 500)}", ct);
        static DateTime? Closed(JsonElement pr) =>
            DateTime.TryParse(Json.Str(pr, "closedDate"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        var prs = list.Where(pr => from is null || Closed(pr) >= from).OrderByDescending(Closed).Take(top).ToList();
        var label = $"{Code.Short(target)}{(repo is null ? "" : $" ({repo})")}";
        if (prs.Count == 0) return $"no pull requests completed into {label} in {name}{(from is null ? "" : $" since {from.Value.ToLocalTime():yyyy-MM-dd}")}";

        var items = await Task.WhenAll(prs.Select(async pr =>
        {
            try
            {
                var linked = await session.Client.GetListAsync($"_apis/git/repositories/{Json.Str(Json.Obj(pr, "repository"), "id")}/pullRequests/{pr.GetProperty("pullRequestId").GetInt32()}/workitems", ct);
                return linked.Select(w => int.TryParse(Json.Str(w, "id"), out var i) ? i : 0).Where(i => i > 0).ToList();
            }
            catch (AdoApiException) { return []; }
        }));
        var ids = items.SelectMany(x => x).Distinct().ToList();
        var heads = ids.Count == 0 ? [] : (await session.Work.GetAsync(ids, relations: false, ["System.Id", "System.WorkItemType", "System.State", "System.Title"], ct))
            .ToDictionary(WorkItems.Id, w => $"#{WorkItems.Id(w)} {Formatting.Collapse(WorkItems.Field(w, "System.Title") ?? "", 50)} ({WorkItems.Field(w, "System.State")})");
        var sb = new StringBuilder($"{prs.Count} pull request(s) completed into {label} in {name}{(from is null ? "" : $" since {from.Value.ToLocalTime():yyyy-MM-dd}")}, newest first:\n");
        for (var i = 0; i < prs.Count; i++)
        {
            var pr = prs[i];
            var wi = items[i].Where(heads.ContainsKey).Select(x => heads[x]).ToList();
            sb.AppendLine($"- PR {pr.GetProperty("pullRequestId").GetInt32()} {Closed(pr):yyyy-MM-dd} {Json.Str(Json.Obj(pr, "repository"), "name")}: " +
                          $"{Formatting.Collapse(Json.Str(pr, "title") ?? "", 70)} ({Json.Str(Json.Obj(pr, "createdBy"), "displayName")}, from {Formatting.Collapse(Code.Short(Json.Str(pr, "sourceRefName")) ?? "", 40)})" +
                          (wi.Count > 0 ? " -> " + string.Join("; ", wi) : " - no work item"));
        }
        if (list.Count >= (from is null ? top : 500) && prs.Count == top) sb.Append($"(top={top}; more with a higher top or a later since)");
        return sb.ToString().TrimEnd();
    }

    static async Task<JsonElement> GetAsync(Session session, string? repo, int id, CancellationToken ct)
    {
        var repoId = repo is null ? null : (await session.Code.RepoAsync(repo, session.Config.Project, ct)).Id;
        return await session.Client.GetAsync(repoId is null ? $"_apis/git/pullrequests/{id}" : $"_apis/git/repositories/{repoId}/pullrequests/{id}", ct);
    }

    /// <summary>Threads people wrote (not the system's notes about votes and pushes, nor conflict resolution records).</summary>
    static List<JsonElement> HumanThreads(List<JsonElement> threads) =>
        threads.Where(t => !Json.Bool(t, "isDeleted") && Json.Arr(t, "comments").Any(c => Json.Str(c, "commentType") == "text"
            && !(Json.Str(c, "content") ?? "").StartsWith("Submitted conflict resolution", StringComparison.Ordinal))).ToList();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLink();

    static string Plain(string? markdown) => Formatting.Collapse(MarkdownLink().Replace(markdown ?? "", "$1"), 200);

    public static async Task<string> ShowAsync(Lens lens, string? connection, int id, string? repo, string? detail, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var client = session.Client;
        var pr = await GetAsync(session, repo, id, ct);
        var repoEl = Json.Obj(pr, "repository");
        var repoId = Json.Str(repoEl, "id")!;
        var project = Json.Str(Json.Obj(repoEl, "project"), "name") ?? session.Project(null);
        var projectId = Json.Str(Json.Obj(repoEl, "project"), "id");
        var baseUrl = $"_apis/git/repositories/{repoId}/pullRequests/{id}";

        var threadsTask = client.GetListAsync(baseUrl + "/threads", ct);
        var workTask = client.GetListAsync(baseUrl + "/workitems", ct);
        var policyTask = projectId is null ? Task.FromResult(new List<JsonElement>())
            : TryList(() => client.GetListAsync($"{AdoClient.Scope(project)}_apis/policy/evaluations?artifactId=vstfs:///CodeReview/CodeReviewId/{projectId}/{id}&api-version=7.1-preview.1", ct));

        var sb = new StringBuilder();
        sb.AppendLine($"PR {id} [{Json.Str(repoEl, "name")}] {Json.Str(pr, "title")}");
        sb.AppendLine($"{Json.Str(pr, "status")}{(Json.Bool(pr, "isDraft") ? ", draft" : "")}: {Code.Short(Json.Str(pr, "targetRefName"))} <- {Code.Short(Json.Str(pr, "sourceRefName"))}, " +
                      $"by {Json.Str(Json.Obj(pr, "createdBy"), "displayName")} {Ago(Json.Str(pr, "creationDate"))} ago, merge {Json.Str(pr, "mergeStatus") ?? "?"}");
        sb.AppendLine($"https://dev.azure.com/{session.Config.Organization}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(Json.Str(repoEl, "name") ?? "")}/pullrequest/{id}");
        var description = Json.Str(pr, "description") ?? "";
        if (description.Length > 0) sb.AppendLine("description:").AppendLine(description.Length > 1500 && detail != "full" ? description[..1500] + "…" : description.TrimEnd());

        var reviewers = Json.Arr(pr, "reviewers").ToList();
        if (reviewers.Count > 0)
            sb.AppendLine("reviewers: " + string.Join("; ", reviewers.Select(r => $"{Json.Str(r, "displayName")} {VoteText(r.TryGetProperty("vote", out var v) ? v.GetInt32() : 0)}{(Json.Bool(r, "isRequired") ? " (required)" : "")}")));
        var policies = await policyTask;
        if (policies.Count > 0)
            sb.AppendLine("checks: " + string.Join("; ", policies.Select(p => $"{Json.Str(Json.Obj(Json.Obj(p, "configuration"), "type"), "displayName")} {Json.Str(p, "status")}")));
        var workIds = (await workTask).Select(w => int.TryParse(Json.Str(w, "id"), out var i) ? i : 0).Where(i => i > 0).ToList();
        if (workIds.Count > 0)
        {
            var items = await session.Work.GetAsync(workIds, relations: false, ["System.Id", "System.WorkItemType", "System.State", "System.Title", "System.TeamProject"], ct);
            sb.AppendLine("work items: " + string.Join("; ", items.Select(WorkItemFormat.Header)));
        }
        else sb.AppendLine("work items: none linked");

        var threads = HumanThreads(await threadsTask);
        var open = threads.Where(t => Json.Str(t, "status") is "active" or "pending").ToList();
        sb.AppendLine($"comments: {open.Count} open thread(s), {threads.Count - open.Count} resolved");
        foreach (var t in (detail == "full" ? threads : open).Take(detail == "full" ? 200 : 20))
        {
            var ctx = Json.Obj(t, "threadContext");
            var where = Json.Str(ctx, "filePath") is { } path
                ? $"{(AotDiff.IsAot(path) ? AotDiff.ObjectName(path) : path[(path.LastIndexOf('/') + 1)..])}" +
                  (Json.Obj(Json.Obj(ctx, "rightFileStart"), "line") is { ValueKind: JsonValueKind.Number } line ? $":{line.GetInt32()}" : "")
                : "general";
            var comments = Json.Arr(t, "comments").Where(c => Json.Str(c, "commentType") == "text").ToList();
            var first = comments[0];
            sb.AppendLine($"- thread {t.GetProperty("id").GetInt32()} [{Json.Str(t, "status")}] {where} - {Json.Str(Json.Obj(first, "author"), "displayName")}: " +
                          $"{Plain(Json.Str(first, "content"))}{(comments.Count > 1 ? $" ({comments.Count - 1} repl{(comments.Count == 2 ? "y" : "ies")}, last: {Plain(Json.Str(comments[^1], "content"))})" : "")}");
        }
        sb.Append($"ado_changes change=\"PR {id}\" lists the files and diffs.");
        return sb.ToString();
    }

    static async Task<List<JsonElement>> TryList(Func<Task<List<JsonElement>>> f)
    {
        try { return await f(); }
        catch (AdoApiException) { return []; }
    }

    [GeneratedRegex(@"@\[(?<name>[^\]]+)\]")]
    private static partial Regex Mention();

    /// <summary>"@[name]" in PR markdown becomes "@&lt;id&gt;", which notifies the person.</summary>
    static async Task<string> MentionsAsync(Identities people, string text, CancellationToken ct)
    {
        var result = text;
        foreach (Match m in Mention().Matches(text))
            result = result.Replace(m.Value, $"@<{(await people.ResolveAsync(m.Groups["name"].Value, ct)).Id}>");
        return result;
    }

    public sealed record PrAction
    {
        public required string Action { get; init; }
        public int? Id { get; init; }
        public string? Repo { get; init; }
        public string? Text { get; init; }
        public int? Thread { get; init; }
        public string? File { get; init; }
        public int? Line { get; init; }
        public string? Vote { get; init; }
        public string? Status { get; init; }
        public string? Source { get; init; }
        public string? Target { get; init; }
        public string? Title { get; init; }
        public string? WorkItems { get; init; }
        public string? Reviewers { get; init; }
        public bool Draft { get; init; }
        public bool DeleteSourceBranch { get; init; }
        public string? MergeStrategy { get; init; }
    }

    public static async Task<string> ActAsync(Lens lens, string? connection, PrAction a, CancellationToken ct = default)
    {
        var action = a.Action.Trim().ToLowerInvariant();
        if (!Actions.Contains(action)) throw new ArgumentException($"action is one of: {string.Join(", ", Actions)}");
        var session = lens.For(connection);
        var client = session.Client;

        if (action == "create")
        {
            if (a.Repo is null || a.Source is null || a.Target is null || string.IsNullOrWhiteSpace(a.Title))
                throw new ArgumentException("create needs repo, source, target and title");
            var repo = await session.Code.RepoAsync(a.Repo, session.Config.Project, ct);
            var reviewers = new List<object>();
            foreach (var r in (a.Reviewers ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                reviewers.Add(new { id = (await session.People.ResolveAsync(r, ct)).Id });
            var body = new Dictionary<string, object?>
            {
                ["sourceRefName"] = "refs/heads/" + a.Source.Replace("refs/heads/", ""),
                ["targetRefName"] = "refs/heads/" + a.Target.Replace("refs/heads/", ""),
                ["title"] = a.Title.Trim(),
                ["description"] = a.Text is null ? null : await MentionsAsync(session.People, a.Text, ct),
                ["isDraft"] = a.Draft,
                ["workItemRefs"] = WorkQueries.ParseIds(a.WorkItems).Select(i => new { id = i.ToString(CultureInfo.InvariantCulture) }).ToArray(),
                ["reviewers"] = reviewers,
            };
            var pr = await client.PostAsync($"_apis/git/repositories/{repo.Id}/pullrequests", body, ct);
            var words = Html.Words(a.Text);
            return $"created {Line(pr)}" + (WorkQueries.ParseIds(a.WorkItems).Count == 0 ? "\nnote: no work item linked (branch policies often require one)" : "")
                   + (words > 150 ? $"\nnote: description has {words} words; usually ≤ 150 - what and why, changes, how to check" : "");
        }

        if (a.Id is not { } id) throw new ArgumentException($"{action} needs the PR id");
        var current = await GetAsync(session, a.Repo, id, ct);
        var repoId = Json.Str(Json.Obj(current, "repository"), "id")!;
        var baseUrl = $"_apis/git/repositories/{repoId}/pullrequests/{id}";
        switch (action)
        {
            case "comment":
            {
                if (string.IsNullOrWhiteSpace(a.Text)) throw new ArgumentException("comment needs text");
                var thread = new Dictionary<string, object?>
                {
                    ["comments"] = new[] { new { parentCommentId = 0, content = await MentionsAsync(session.People, a.Text, ct), commentType = 1 } },
                    ["status"] = 1,
                };
                if (a.File is not null)
                {
                    var path = "/" + a.File.Trim().TrimStart('/');
                    thread["threadContext"] = a.Line is { } line
                        ? new { filePath = path, rightFileStart = new { line, offset = 1 }, rightFileEnd = new { line, offset = 1 } }
                        : new { filePath = path };
                }
                var t = await client.PostAsync(baseUrl + "/threads", thread, ct);
                return $"commented on PR {id} (thread {t.GetProperty("id").GetInt32()}{(a.File is null ? "" : $", {a.File}{(a.Line is { } l ? $":{l}" : "")}")})";
            }
            case "reply":
                if (a.Thread is not { } th || string.IsNullOrWhiteSpace(a.Text)) throw new ArgumentException("reply needs thread and text");
                await client.PostAsync($"{baseUrl}/threads/{th}/comments", new { parentCommentId = 1, content = await MentionsAsync(session.People, a.Text, ct), commentType = 1 }, ct);
                return $"replied in thread {th} of PR {id}";
            case "resolve":
            {
                if (a.Thread is not { } th2) throw new ArgumentException("resolve needs thread");
                var status = (a.Status ?? "fixed").Trim().ToLowerInvariant();
                if (status is not ("fixed" or "closed" or "wontfix" or "bydesign" or "active" or "pending"))
                    throw new ArgumentException("status is fixed, closed, wontFix, byDesign, active or pending");
                await client.SendAsync(HttpMethod.Patch, $"{baseUrl}/threads/{th2}", new { status = status switch { "wontfix" => "wontFix", "bydesign" => "byDesign", _ => status } }, "application/json", ct);
                return $"thread {th2} of PR {id} is now {status}";
            }
            case "vote":
            {
                if (a.Vote is null || !Votes.TryGetValue(a.Vote.Trim(), out var vote)) throw new ArgumentException($"vote is one of: {string.Join(", ", Votes.Keys)}");
                var (me, _) = await new Boards(session).MeAsync(ct);
                await client.SendAsync(HttpMethod.Put, $"{baseUrl}/reviewers/{me}", new { vote }, "application/json", ct);
                return $"voted on PR {id}: {VoteText(vote)}";
            }
            case "publish":
                await client.SendAsync(HttpMethod.Patch, baseUrl, new { isDraft = false }, "application/json", ct);
                return $"PR {id} is published (no longer a draft)";
            case "abandon":
                await client.SendAsync(HttpMethod.Patch, baseUrl, new { status = "abandoned" }, "application/json", ct);
                return $"PR {id} abandoned";
            default: // complete
            {
                var lastCommit = Json.Str(Json.Obj(current, "lastMergeSourceCommit"), "commitId");
                var body = new Dictionary<string, object?>
                {
                    ["status"] = "completed",
                    ["lastMergeSourceCommit"] = new { commitId = lastCommit },
                    ["completionOptions"] = new Dictionary<string, object?>
                    {
                        ["deleteSourceBranch"] = a.DeleteSourceBranch,
                        ["mergeStrategy"] = a.MergeStrategy,
                        ["transitionWorkItems"] = false,
                    },
                };
                var done = await client.SendAsync(HttpMethod.Patch, baseUrl, body, "application/json", ct);
                return $"PR {id}: {Json.Str(done, "status")}{(Json.Str(done, "mergeStatus") is { } ms ? $", merge {ms}" : "")}";
            }
        }
    }
}
