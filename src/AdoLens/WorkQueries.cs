using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>The answers behind the work item tools (same text in the CLI).</summary>
public static partial class WorkQueries
{
    public const int MaxItemsWithDetails = 20;
    public static readonly string[] Details = ["summary", "normal", "full"];

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    /// <summary>"123; #456, 789" → [123, 456, 789].</summary>
    public static List<int> ParseIds(string? text) =>
        text is null ? [] : Number().Matches(text).Select(m => int.Parse(m.Value)).Where(i => i > 0).Distinct().ToList();

    static string Detail(string? detail)
    {
        var d = (detail ?? "normal").Trim().ToLowerInvariant();
        return Details.Contains(d) ? d : throw new ArgumentException($"detail is one of: {string.Join(", ", Details)}");
    }

    /// <summary>Metadata of the project an item belongs to; null when it cannot be read (the answer then shows reference names).</summary>
    static async Task<Snapshot?> SnapshotAsync(Session session, string? project, CancellationToken ct)
    {
        if (project is null) return null;
        try { return (await session.Meta.GetAsync(project, ct)).Snapshot; }
        catch (AdoApiException) { return null; }
    }

    static async Task<Dictionary<int, string>> HeadersAsync(WorkItems work, IEnumerable<int> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return [];
        var items = await work.GetAsync(list, relations: false, ["System.Id", "System.WorkItemType", "System.State", "System.Title", "System.TeamProject"], ct);
        return items.ToDictionary(WorkItems.Id, WorkItemFormat.Header);
    }

    public static async Task<string> ItemAsync(Lens lens, string? connection, string ids, string? include, string? detail, CancellationToken ct = default)
    {
        var d = Detail(detail);
        var wanted = ParseIds(ids);
        if (wanted.Count == 0) throw new ArgumentException("give one or more work item ids, e.g. 123 or 123;456");
        var session = lens.For(connection);
        var work = session.Work;
        var inc = (include ?? "").ToLowerInvariant();

        if (wanted.Count > MaxItemsWithDetails && d != "summary") d = "summary";
        var items = await work.GetAsync(wanted, relations: d != "summary", ct: ct);
        if (items.Count == 0) return $"no work item {string.Join(", ", wanted.Select(i => "#" + i))} (missing, deleted or not visible with this PAT)";

        var headers = d == "summary" ? [] : await HeadersAsync(work, items.SelectMany(WorkItemFormat.LinkedIds), ct);
        var sb = new StringBuilder();
        foreach (var wi in items)
        {
            var project = WorkItems.Field(wi, "System.TeamProject");
            var s = await SnapshotAsync(session, project, ct);
            if (sb.Length > 0) sb.AppendLine(d == "summary" ? "" : "\n---");
            sb.AppendLine(WorkItemFormat.Item(wi, s, headers, d, work.WebBase));
            if (inc.Contains("comment") && project is not null)
            {
                var total = Json.Obj(wi, "fields").TryGetProperty("System.CommentCount", out var cc) && cc.TryGetInt32(out var n) ? n : 0;
                var comments = await work.CommentsAsync(project, WorkItems.Id(wi), d == "full" ? 100 : 10, ct);
                sb.AppendLine(WorkItemFormat.Comments(comments, total));
            }
            if (inc.Contains("history") && project is not null)
                sb.AppendLine(WorkItemFormat.History(await work.UpdatesAsync(project, WorkItems.Id(wi), ct), s, d));
        }
        var missing = wanted.Except(items.Select(WorkItems.Id)).ToList();
        if (missing.Count > 0) sb.AppendLine($"not found: {string.Join(", ", missing.Select(i => "#" + i))}");
        return sb.ToString().TrimEnd();
    }

    public static async Task<string> CreateAsync(Lens lens, string? connection, string? project, string type, WriteRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Title)) throw new ArgumentException("title is required");
        var session = lens.For(connection);
        var name = session.Project(project);
        var (s, _) = await session.Meta.GetAsync(name, ct);
        var t = s.FindType(type) ?? throw new InvalidOperationException(
            $"no work item type '{type}' in {s.Project.Name}; types: {string.Join(", ", s.Types.Where(x => !x.Disabled && x.Level != Levels.Hidden).Select(x => x.Name).Order())}");
        if (t.Disabled) throw new InvalidOperationException($"{t.Name} is disabled in this process");

        var notes = new List<string>();
        var ops = await session.Work.BuildOpsAsync(s, t, r, null, notes, ct);
        var created = await session.Work.CreateAsync(s.Project.Name, t.Name, ops, ct);
        Audit.Write(lens, session.Config.Connection, [new AuditEntry { Batch = Audit.NewBatch(), Tool = "ado_create", Kind = "create",
            Id = WorkItems.Id(created), Project = s.Project.Name, Title = WorkItems.Field(created, "System.Title") }]);
        return Written("created", created, s, session.Work, notes);
    }

    static string Written(string verb, JsonElement wi, Snapshot? s, WorkItems work, List<string> notes)
    {
        var sb = new StringBuilder($"{verb} ").Append(WorkItemFormat.Item(wi, s, new Dictionary<int, string>(), "summary", work.WebBase));
        foreach (var n in notes) sb.Append("\nnote: ").Append(n);
        return sb.ToString();
    }

    /// <summary>
    /// Updates one or many work items. Above <paramref name="confirmAbove"/> items nothing is written without the token
    /// of a preview of exactly the same change: the first call previews, the second (with confirm) writes.
    /// </summary>
    public static async Task<string> UpdateAsync(Lens lens, string? connection, string? ids, string? wiql, WriteRequest r, string? comment,
        string? confirm, int confirmAbove, CancellationToken ct = default)
    {
        if (r.IsEmpty && string.IsNullOrWhiteSpace(comment)) throw new ArgumentException("nothing to change: give fields, state, assignedTo, tags, … or a comment");
        var session = lens.For(connection);
        var work = session.Work;
        var target = ParseIds(ids);
        if (wiql is not null)
        {
            var project = session.Project(null);
            target.AddRange((await work.WiqlAsync(project, wiql, 1000, ct)).Ids);
            target = target.Distinct().ToList();
        }
        if (target.Count == 0) return "no work items to update (no ids given or the query found none)";

        var items = await work.GetAsync(target, relations: false, ct: ct);
        if (items.Count == 0) return $"no work item {string.Join(", ", target.Select(i => "#" + i))} visible with this PAT";

        // Operations per item, checked before anything is written: one bad item stops the whole change.
        var plans = new List<(JsonElement Item, List<object> Ops, Snapshot S, List<string> Notes)>();
        foreach (var wi in items)
        {
            var project = WorkItems.Field(wi, "System.TeamProject") ?? session.Project(null);
            var s = (await session.Meta.GetAsync(project, ct)).Snapshot;
            var typeName = WorkItems.Field(wi, "System.WorkItemType") ?? "";
            var t = s.FindType(typeName) ?? throw new InvalidOperationException($"#{WorkItems.Id(wi)}: type {typeName} is not in the metadata (refresh with ado_fields refresh=true)");
            var notes = new List<string>();
            List<object> ops;
            try { ops = r.IsEmpty ? [] : await work.BuildOpsAsync(s, t, r, wi, notes, ct); }
            catch (InvalidOperationException ex) { throw new InvalidOperationException($"#{WorkItems.Id(wi)}: {ex.Message}"); }
            plans.Add((wi, ops, s, notes));
        }

        var ordered = items.Select(WorkItems.Id).ToList();
        var token = WorkItems.Token(ordered, r, comment);
        if (items.Count > confirmAbove && !string.Equals(confirm?.Trim(), token, StringComparison.OrdinalIgnoreCase))
            return Preview(plans, r, comment, token, confirm is not null);

        var results = new List<string>();
        var failed = 0;
        var batch = Audit.NewBatch();
        var log = new List<AuditEntry>();
        await Parallel.ForEachAsync(plans, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (p, token2) =>
        {
            var id = WorkItems.Id(p.Item);
            try
            {
                var updated = p.Ops.Count > 0 ? await work.PatchAsync(id, p.Ops, token2) : p.Item;
                if (p.Ops.Count > 0) lock (log) log.AddRange(Audit.FromOps(batch, "ado_update", p.Item, updated, p.Ops));
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    var project = WorkItems.Field(p.Item, "System.TeamProject")!;
                    var c = await work.AddCommentAsync(project, id, await CommentHtmlAsync(session.People, comment, token2), token2);
                    lock (log) log.Add(CommentEntry(batch, "ado_update", p.Item, project, c));
                }
                lock (results) results.Add(items.Count == 1 ? Written("updated", updated, p.S, work, p.Notes) : "ok " + WorkItemFormat.Header(updated));
            }
            catch (Exception ex) when (ex is AdoApiException or InvalidOperationException)
            {
                lock (results) { results.Add($"FAILED #{id}: {ex.Message}"); failed++; }
            }
        });
        Audit.Write(lens, session.Config.Connection, log);
        if (items.Count == 1) return results[0];
        var order = ordered.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return $"updated {items.Count - failed} of {items.Count}{(failed > 0 ? $", {failed} failed" : "")}:\n" +
               string.Join('\n', results.OrderBy(l => order.GetValueOrDefault(ParseIds(l).FirstOrDefault(), int.MaxValue)));
    }

    static string Preview(List<(JsonElement Item, List<object> Ops, Snapshot S, List<string> Notes)> plans, WriteRequest r, string? comment, string token, bool staleToken)
    {
        var sb = new StringBuilder();
        if (staleToken) sb.AppendLine("the confirm token does not match this change (items or values changed) - new preview:");
        sb.AppendLine($"PREVIEW - nothing written yet. {plans.Count} work items would change:");
        foreach (var (wi, ops, _, _) in plans.Take(30))
        {
            var changes = ops.Select(o => JsonSerializer.SerializeToElement(o)).Select(o =>
            {
                var path = Json.Str(o, "path") ?? "";
                var reference = path.StartsWith("/fields/", StringComparison.Ordinal) ? path[8..] : path;
                var before = WorkItems.Field(wi, reference);
                var after = o.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.Object ? "link" : Json.Scalar(v)) : "-";
                if (reference is WorkItems.DescriptionRef or WorkItems.AcceptanceRef) return $"{reference.Split('.')[^1]} replaced";
                return $"{reference.Split('.')[^1]}: {Formatting.Collapse(before ?? "-", 40)} -> {Formatting.Collapse(after ?? "-", 40)}";
            }).ToList();
            if (!string.IsNullOrWhiteSpace(comment)) changes.Add("comment added");
            sb.AppendLine($"- {WorkItemFormat.Header(wi)}: {string.Join("; ", changes)}");
        }
        if (plans.Count > 30) sb.AppendLine($"- … {plans.Count - 30} more");
        sb.Append($"Show this to the user. To write it, call again with the same arguments and confirm={token}.");
        return sb.ToString();
    }

    [GeneratedRegex(@"@\[(?<name>[^\]]+)\]")]
    private static partial Regex Mention();

    /// <summary>Markdown comment to HTML; "@[name]" becomes a real mention (the person is notified).</summary>
    public static async Task<string> CommentHtmlAsync(Identities people, string markdown, CancellationToken ct)
    {
        var raw = new Dictionary<string, string>();
        var i = 0;
        var text = markdown;
        foreach (Match m in Mention().Matches(markdown))
        {
            var person = await people.ResolveAsync(m.Groups["name"].Value, ct);
            var key = $"\u0001MENTION{i++}\u0001";
            raw[key] = $"<a href=\"#\" data-vss-mention=\"version:2.0,{person.Id}\">@{System.Net.WebUtility.HtmlEncode(person.DisplayName)}</a>";
            text = text.Replace(m.Value, key);
        }
        return Html.FromMarkdown(text, raw);
    }

    static AuditEntry CommentEntry(string batch, string tool, JsonElement wi, string project, JsonElement comment) => new()
    {
        Batch = batch, Tool = tool, Kind = "comment", Id = WorkItems.Id(wi), Project = project, Title = WorkItems.Field(wi, "System.Title"),
        CommentId = comment.ValueKind == JsonValueKind.Object && comment.TryGetProperty("id", out var cid) && cid.TryGetInt32(out var n) ? n : null,
    };

    public static async Task<string> CommentAsync(Lens lens, string? connection, int id, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text is required");
        var session = lens.For(connection);
        var items = await session.Work.GetAsync([id], relations: false, ["System.Id", "System.TeamProject", "System.WorkItemType", "System.State", "System.Title"], ct);
        if (items.Count == 0) return $"no work item #{id} visible with this PAT";
        var project = WorkItems.Field(items[0], "System.TeamProject")!;
        var html = await CommentHtmlAsync(session.People, text, ct);
        var added = await session.Work.AddCommentAsync(project, id, html, ct);
        Audit.Write(lens, session.Config.Connection, [CommentEntry(Audit.NewBatch(), "ado_comment", items[0], project, added)]);
        var mentions = Mention().Matches(text).Count;
        var note = Html.Words(text) > 60 ? $"\nnote: {Html.Words(text)} words; comments read best at ≤ 60 (decision or question first)" : "";
        return $"commented on {WorkItemFormat.Header(items[0])}{(mentions > 0 ? $" ({mentions} mention(s) notified)" : "")}{note}";
    }

    static readonly Dictionary<string, string> LinkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["parent"] = "System.LinkTypes.Hierarchy-Reverse",
        ["child"] = "System.LinkTypes.Hierarchy-Forward",
        ["related"] = "System.LinkTypes.Related",
        ["predecessor"] = "System.LinkTypes.Dependency-Reverse",
        ["successor"] = "System.LinkTypes.Dependency-Forward",
        ["duplicate"] = "System.LinkTypes.Duplicate-Forward",
        ["duplicate-of"] = "System.LinkTypes.Duplicate-Reverse",
        ["tested-by"] = "Microsoft.VSTS.Common.TestedBy-Forward",
        ["tests"] = "Microsoft.VSTS.Common.TestedBy-Reverse",
    };

    public static string LinkTypeNames => string.Join(", ", LinkTypes.Keys);

    public static async Task<string> LinkAsync(Lens lens, string? connection, int id, string targets, string type, bool remove, CancellationToken ct = default)
    {
        var rel = LinkTypes.GetValueOrDefault(type.Trim()) ?? throw new ArgumentException($"link type is one of: {LinkTypeNames}");
        var ids = ParseIds(targets).Where(t => t != id).ToList();
        if (ids.Count == 0) throw new ArgumentException("give the work item(s) to link to");
        var session = lens.For(connection);
        var work = session.Work;
        var items = await work.GetAsync([id], relations: true, ct: ct);
        if (items.Count == 0) return $"no work item #{id} visible with this PAT";
        var wi = items[0];
        var relations = Json.Arr(wi, "relations").ToList();
        int IndexOf(int target) => relations.FindIndex(x => Json.Str(x, "rel") == rel && (Json.Str(x, "url") ?? "").EndsWith("/" + target, StringComparison.Ordinal));

        var ops = new List<object>();
        var skipped = new List<string>();
        if (remove)
        {
            // Highest index first: removing shifts the indexes behind it.
            foreach (var (t, i) in ids.Select(t => (t, IndexOf(t))).OrderByDescending(x => x.Item2))
                if (i >= 0) ops.Add(new { op = "remove", path = $"/relations/{i}" });
                else skipped.Add($"#{t} (not linked as {type})");
        }
        else
        {
            if (rel == LinkTypes["parent"])
            {
                if (ids.Count > 1) throw new ArgumentException("a work item has one parent");
                if (relations.Any(x => Json.Str(x, "rel") == rel) && IndexOf(ids[0]) < 0)
                    throw new InvalidOperationException($"#{id} already has a parent; remove it first (remove=true) or link the other way");
            }
            foreach (var t in ids)
                if (IndexOf(t) >= 0) skipped.Add($"#{t} (already linked)");
                else ops.Add(new { op = "add", path = "/relations/-", value = new { rel, url = $"{work.WebBase}_apis/wit/workItems/{t}" } });
        }
        if (ops.Count > 0)
        {
            await work.PatchAsync(id, ops, ct);
            var batch = Audit.NewBatch();
            Audit.Write(lens, session.Config.Connection, ids.Where(t => !skipped.Any(x => x.StartsWith($"#{t} ", StringComparison.Ordinal)))
                .Select(t => new AuditEntry { Batch = batch, Tool = "ado_link", Kind = remove ? "link-remove" : "link-add", Id = id,
                    Title = WorkItems.Field(wi, "System.Title"), Rel = rel, Target = t }));
        }
        var headers = await HeadersAsync(work, ids, ct);
        var done = ids.Where(t => !skipped.Any(x => x.StartsWith($"#{t} ", StringComparison.Ordinal))).Select(t => headers.GetValueOrDefault(t, $"#{t}")).ToList();
        var sb = new StringBuilder($"{WorkItemFormat.Header(wi)}: ");
        sb.Append(done.Count == 0 ? "nothing changed" : $"{(remove ? "removed" : "added")} {type}: {string.Join("; ", done)}");
        if (skipped.Count > 0) sb.Append($"\nskipped: {string.Join(", ", skipped)}");
        return sb.ToString();
    }

    public static async Task<string> QueryAsync(Lens lens, string? connection, string? project, string? wiql, string? query, int top, CancellationToken ct = default)
    {
        if (wiql is null == (query is null)) throw new ArgumentException("give either wiql or query (a saved query path or id)");
        var session = lens.For(connection);
        var name = session.Project(project);
        top = Math.Clamp(top, 1, 500);
        List<int> ids;
        List<string> columns;
        string title;
        if (wiql is not null)
        {
            (ids, columns) = await session.Work.WiqlAsync(name, wiql, 1000, ct);
            title = "query";
        }
        else (ids, columns, title) = await session.Work.SavedQueryAsync(name, query!, 1000, ct);

        var standard = new[] { "System.Id", "System.WorkItemType", "System.State", "System.Title", "System.AssignedTo", "System.IterationPath", "System.TeamProject" };
        var extra = columns.Where(c => !standard.Contains(c, StringComparer.OrdinalIgnoreCase)).Take(4).ToList();
        var items = await session.Work.GetAsync(ids.Take(top).ToList(), relations: false, standard.Concat(extra).ToList(), ct);
        var s = await SnapshotAsync(session, name, ct);
        return WorkItemFormat.Table(items, extra, s, ids.Count, title);
    }
}
