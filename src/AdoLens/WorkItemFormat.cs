using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AdoLens;

/// <summary>Compact text for work items: header, filled fields only, rich text as markdown, links summarized.</summary>
public static class WorkItemFormat
{
    /// <summary>Shown in the header or the facts line, never again in the field list.</summary>
    static readonly HashSet<string> Shown = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Id", "System.WorkItemType", "System.State", "System.Title", "System.AssignedTo", "System.AreaPath",
        "System.IterationPath", "System.Tags", "System.Reason", "System.TeamProject", "System.BoardColumn",
        "System.CreatedDate", "System.CreatedBy", "System.ChangedDate", "System.ChangedBy",
        WorkItems.DescriptionRef, WorkItems.AcceptanceRef, WorkItems.ReproStepsRef, "System.History",
    };

    /// <summary>Bookkeeping that tells nothing about the work (shown with detail=full only).</summary>
    static bool IsNoise(string reference) =>
        reference.StartsWith("WEF_", StringComparison.OrdinalIgnoreCase)
        || reference is "System.Rev" or "System.AreaId" or "System.IterationId" or "System.NodeName" or "System.AuthorizedDate"
            or "System.RevisedDate" or "System.AuthorizedAs" or "System.PersonId" or "System.Watermark" or "System.CommentCount"
            or "System.BoardColumnDone" or "System.Parent" or "System.ExternalLinkCount" or "System.RemoteLinkCount"
            or "System.HyperLinkCount" or "System.AttachedFileCount" or "System.RelatedLinkCount" or "System.BoardLane"
            or "Microsoft.VSTS.Common.StateChangeDate" or "Microsoft.VSTS.Common.ActivatedDate" or "Microsoft.VSTS.Common.ActivatedBy"
            or "Microsoft.VSTS.Common.ResolvedDate" or "Microsoft.VSTS.Common.ResolvedBy" or "Microsoft.VSTS.Common.ClosedDate"
            or "Microsoft.VSTS.Common.ClosedBy" or "Microsoft.VSTS.Common.BacklogPriority" or "Microsoft.VSTS.Common.StackRank"
        || reference.StartsWith("System.AreaLevel", StringComparison.Ordinal) || reference.StartsWith("System.IterationLevel", StringComparison.Ordinal);

    public static string Url(string webBase, JsonElement wi) =>
        $"{webBase}{Uri.EscapeDataString(WorkItems.Field(wi, "System.TeamProject") ?? "")}/_workitems/edit/{WorkItems.Id(wi)}";

    /// <summary>"#123 Bug [Active] Title".</summary>
    public static string Header(JsonElement wi) =>
        $"#{WorkItems.Id(wi)} {WorkItems.Field(wi, "System.WorkItemType")} [{WorkItems.Field(wi, "System.State")}] {WorkItems.Field(wi, "System.Title")?.Trim()}";

    static string ShortPath(string? path, string? project) =>
        path is null ? "" : project is not null && path.StartsWith(project + "\\", StringComparison.OrdinalIgnoreCase) ? path[(project.Length + 1)..] : path;

    static string Date(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : iso ?? "";

    static string? BoardColumn(JsonElement wi)
    {
        if (WorkItems.Field(wi, "System.BoardColumn") is { } col) return col;
        var fields = Json.Obj(wi, "fields");
        return fields.ValueKind != JsonValueKind.Object ? null
            : fields.EnumerateObject().Where(p => p.Name.EndsWith("_Kanban.Column", StringComparison.Ordinal)).Select(p => Json.Scalar(p.Value)).FirstOrDefault(v => v is not null);
    }

    /// <summary>One work item. <paramref name="related"/> holds headers of linked items (parent, children, related).</summary>
    public static string Item(JsonElement wi, Snapshot? s, IReadOnlyDictionary<int, string> related, string detail, string webBase)
    {
        var sb = new StringBuilder();
        var project = WorkItems.Field(wi, "System.TeamProject");
        sb.AppendLine(Header(wi));
        sb.AppendLine(Url(webBase, wi));

        var facts = new List<string>();
        void Fact(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) facts.Add($"{label}: {value}"); }
        Fact("assigned", WorkItems.Field(wi, "System.AssignedTo"));
        Fact("reason", WorkItems.Field(wi, "System.Reason"));
        Fact("area", ShortPath(WorkItems.Field(wi, "System.AreaPath"), project));
        Fact("iteration", ShortPath(WorkItems.Field(wi, "System.IterationPath"), project));
        Fact("tags", WorkItems.Field(wi, "System.Tags"));
        var column = BoardColumn(wi);
        if (column is not null && !column.Equals(WorkItems.Field(wi, "System.State"), StringComparison.OrdinalIgnoreCase)) Fact("board", column);
        sb.AppendLine(string.Join(" | ", facts));
        if (detail == "summary") return sb.ToString().TrimEnd();

        // Remaining filled fields under their display names.
        var type = s?.FindType(WorkItems.Field(wi, "System.WorkItemType") ?? "");
        var names = type is null ? new Dictionary<string, string>() : s!.FieldsOf(type.Name).ToDictionary(f => f.Ref, f => f.Name, StringComparer.OrdinalIgnoreCase);
        var other = new List<string>();
        if (Json.Obj(wi, "fields") is { ValueKind: JsonValueKind.Object } fields)
            foreach (var p in fields.EnumerateObject())
            {
                if (Shown.Contains(p.Name) || (detail != "full" && IsNoise(p.Name))) continue;
                var value = p.Value.ValueKind == JsonValueKind.Object ? Json.Str(p.Value, "displayName") : Json.Scalar(p.Value);
                if (string.IsNullOrWhiteSpace(value)) continue;
                var label = names.GetValueOrDefault(p.Name) ?? s?.Fields.GetValueOrDefault(p.Name)?.Name ?? p.Name;
                if (s?.Fields.GetValueOrDefault(p.Name)?.Type == "html") value = Formatting.Collapse(Html.ToMarkdown(value), detail == "full" ? 2000 : 300);
                else if (s?.Fields.GetValueOrDefault(p.Name)?.Type == "dateTime") value = Date(value);
                other.Add($"{label}: {value}");
            }
        if (other.Count > 0) sb.AppendLine(string.Join(" | ", other));
        sb.AppendLine($"created {Date(WorkItems.Field(wi, "System.CreatedDate"))} by {WorkItems.Field(wi, "System.CreatedBy")}, " +
                      $"changed {Date(WorkItems.Field(wi, "System.ChangedDate"))} by {WorkItems.Field(wi, "System.ChangedBy")}");

        var max = detail == "full" ? 20000 : 3000;
        Section(sb, "description", WorkItems.Field(wi, WorkItems.DescriptionRef), max);
        Section(sb, "repro steps", WorkItems.Field(wi, WorkItems.ReproStepsRef), max);
        Section(sb, "acceptance criteria", WorkItems.Field(wi, WorkItems.AcceptanceRef), max);

        Relations(sb, wi, related, detail);
        if (Json.Obj(wi, "fields") is { ValueKind: JsonValueKind.Object } f2 && f2.TryGetProperty("System.CommentCount", out var cc) && cc.TryGetInt32(out var n) && n > 0)
            sb.AppendLine($"comments: {n} (include=comments)");
        return sb.ToString().TrimEnd();
    }

    static void Section(StringBuilder sb, string title, string? html, int max)
    {
        var md = Html.ToMarkdown(html);
        if (md.Length == 0) return;
        sb.AppendLine($"{title}:");
        sb.AppendLine(md.Length <= max ? md : md[..max] + $"… ({md.Length - max} more characters, detail=full)");
    }

    /// <summary>Ids of linked work items (parent, children, related…), for fetching their headers.</summary>
    public static List<int> LinkedIds(JsonElement wi) =>
        Json.Arr(wi, "relations").Select(r => Json.Str(r, "url") ?? "").Where(u => u.Contains("/_apis/wit/workItems/", StringComparison.OrdinalIgnoreCase))
            .Select(u => int.TryParse(u[(u.LastIndexOf('/') + 1)..], out var id) ? id : 0).Where(id => id > 0).Distinct().ToList();

    static readonly Dictionary<string, string> RelationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System.LinkTypes.Hierarchy-Reverse"] = "parent",
        ["System.LinkTypes.Hierarchy-Forward"] = "children",
        ["System.LinkTypes.Related"] = "related",
        ["System.LinkTypes.Dependency-Reverse"] = "predecessors",
        ["System.LinkTypes.Dependency-Forward"] = "successors",
        ["System.LinkTypes.Duplicate-Forward"] = "duplicates",
        ["System.LinkTypes.Duplicate-Reverse"] = "duplicate of",
        ["Microsoft.VSTS.Common.TestedBy-Forward"] = "tested by",
        ["Microsoft.VSTS.Common.TestedBy-Reverse"] = "tests",
        ["Microsoft.VSTS.Common.Affects-Forward"] = "affects",
        ["Microsoft.VSTS.Common.Affects-Reverse"] = "affected by",
    };

    static void Relations(StringBuilder sb, JsonElement wi, IReadOnlyDictionary<int, string> related, string detail)
    {
        var groups = new Dictionary<string, List<string>>();
        var code = new List<string>();
        foreach (var r in Json.Arr(wi, "relations"))
        {
            var rel = Json.Str(r, "rel") ?? "";
            var url = Json.Str(r, "url") ?? "";
            var attrs = Json.Obj(r, "attributes");
            if (rel == "ArtifactLink")
            {
                code.Add(Artifact(url, Json.Str(attrs, "name"), Json.Str(attrs, "comment")));
                continue;
            }
            if (rel == "AttachedFile") { Add(groups, "attachments", Json.Str(attrs, "name") ?? "file"); continue; }
            if (rel == "Hyperlink") { Add(groups, "links", url); continue; }
            var id = int.TryParse(url[(url.LastIndexOf('/') + 1)..], out var v) ? v : 0;
            if (id == 0) continue;
            var name = RelationNames.GetValueOrDefault(rel) ?? Json.Str(attrs, "name")?.ToLowerInvariant() ?? rel;
            Add(groups, name, related.TryGetValue(id, out var header) ? header : $"#{id}");
        }
        foreach (var (name, items) in groups.OrderBy(g => g.Key == "parent" ? 0 : g.Key == "children" ? 1 : 2))
        {
            var shown = detail == "full" ? items : items.Take(15).ToList();
            var more = items.Count - shown.Count;
            if (shown.Count == 1 && name is "parent" or "links" or "attachments" || shown.Sum(x => x.Length) < 120)
                sb.AppendLine($"{name}: {string.Join("; ", shown)}{(more > 0 ? $"; … {more} more" : "")}");
            else
            {
                sb.AppendLine($"{name} ({items.Count}):");
                foreach (var x in shown) sb.AppendLine("  " + x);
                if (more > 0) sb.AppendLine($"  … {more} more (detail=full)");
            }
        }
        if (code.Count > 0)
        {
            var shown = detail == "full" ? code : code.Take(10).ToList();
            sb.AppendLine($"code ({code.Count}): {string.Join("; ", shown)}{(code.Count > shown.Count ? $"; … {code.Count - shown.Count} more" : "")}");
        }
    }

    static void Add(Dictionary<string, List<string>> groups, string key, string value)
    {
        if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
        list.Add(value);
    }

    /// <summary>vstfs links in words: "changeset 523 (Fix x)", "PR 482", "commit 1a2b3c4", "build 213864".</summary>
    /// <summary>"releaseId:stageId" → "Release-12 / UAT: succeeded 2026-10-10", filled by <see cref="ReleaseStagesAsync"/>.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ReleaseStages = new();

    static (string Project, string Release, string Stage)? ReleaseStage(string url)
    {
        var plain = Uri.UnescapeDataString(url);
        var i = plain.IndexOf("/ReleaseManagement/ReleaseEnvironment/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var parts = plain[(i + "/ReleaseManagement/ReleaseEnvironment/".Length)..].Split(':');
        return parts.Length == 3 ? (parts[0], parts[1], parts[2]) : null;
    }

    /// <summary>Looks up the release stages the items are linked to (at most 40 releases), for <see cref="Artifact"/>.</summary>
    public static async Task ReleaseStagesAsync(AdoClient client, IEnumerable<JsonElement> items, CancellationToken ct)
    {
        var wanted = items.SelectMany(w => Json.Arr(w, "relations")).Where(r => Json.Str(r, "rel") == "ArtifactLink")
            .Select(r => ReleaseStage(Json.Str(r, "url") ?? "")).Where(x => x is not null).Select(x => x!.Value)
            .Where(x => !ReleaseStages.ContainsKey($"{x.Release}:{x.Stage}"))
            .Where(x => !ReleaseStages.ContainsKey($"{x.Release}:*"))
            .GroupBy(x => x.Release).Take(40).ToList();
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(wanted.Select(async g =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var release = await client.GetAsync(client.ServiceUrl("vsrm") + $"{g.First().Project}/_apis/release/releases/{g.Key}", ct);
                foreach (var env in Json.Arr(release, "environments"))
                {
                    var steps = Json.Arr(env, "deploySteps").ToList();
                    var when = (steps.Count > 0 ? Json.Str(steps[^1], "lastModifiedOn") : null) ?? Json.Str(env, "modifiedOn");
                    var date = DateTime.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                        ? " " + d.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "";
                    ReleaseStages[$"{g.Key}:{(env.TryGetProperty("id", out var id) ? id.ToString() : "")}"] =
                        $"{Json.Str(release, "name")} / {Json.Str(env, "name")}: {Json.Str(env, "status")}{date}";
                }
            }
            // Old releases are deleted by retention; other failures (permission) are named instead of hidden.
            catch (AdoApiException ex)
            {
                ReleaseStages[$"{g.Key}:*"] = ex.Status == System.Net.HttpStatusCode.NotFound ? "release no longer kept" : $"release not readable ({(int?)ex.Status})";
            }
            finally { gate.Release(); }
        }));
    }

    public static string Artifact(string url, string? name, string? comment)
    {
        // Git artifacts escape their parts: vstfs:///Git/Commit/{project}%2F{repo}%2F{sha}.
        var plain = Uri.UnescapeDataString(url);
        var last = plain[(plain.LastIndexOf('/') + 1)..];
        string text = url switch
        {
            _ when url.Contains("/VersionControl/Changeset/", StringComparison.OrdinalIgnoreCase) => $"changeset {last}",
            _ when url.Contains("/Git/PullRequestId/", StringComparison.OrdinalIgnoreCase) => $"PR {last}",
            _ when url.Contains("/Git/Commit/", StringComparison.OrdinalIgnoreCase) => $"commit {last}",
            _ when ReleaseStage(url) is { } rs => ReleaseStages.TryGetValue($"{rs.Release}:{rs.Stage}", out var known)
                ? $"release {known}"
                : $"release {rs.Release}, stage {rs.Stage}" + (ReleaseStages.TryGetValue($"{rs.Release}:*", out var why) ? $" ({why})" : ""),
            _ when url.Contains("/Git/Ref/", StringComparison.OrdinalIgnoreCase) && plain.IndexOf("/GB", StringComparison.Ordinal) is var gb and >= 0 => $"branch {plain[(gb + 3)..]}",
            _ when url.Contains("/Build/Build/", StringComparison.OrdinalIgnoreCase) => $"build {last}",
            _ when url.Contains("/Wiki/", StringComparison.OrdinalIgnoreCase) => "wiki page",
            _ => name ?? url,
        };
        // The automatic comment of build links says nothing; changeset and commit comments are their messages.
        if (!string.IsNullOrWhiteSpace(comment) && !comment.StartsWith("This build was automatically linked", StringComparison.Ordinal))
            text += $" \"{Formatting.Collapse(comment, 60)}\"";
        return text;
    }

    public static string Comments(List<JsonElement> comments, int total)
    {
        if (comments.Count == 0) return "no comments";
        var sb = new StringBuilder($"comments ({(total > comments.Count ? $"last {comments.Count} of {total}" : comments.Count.ToString(CultureInfo.InvariantCulture))}):\n");
        foreach (var c in comments)
        {
            var who = Json.Str(Json.Obj(c, "createdBy"), "displayName");
            var when = Date(Json.Str(c, "createdDate"));
            var text = Html.ToMarkdown(Json.Str(c, "text"));
            sb.AppendLine($"- {when} {who}: {Formatting.Collapse(text, 800)}");
        }
        return sb.ToString().TrimEnd();
    }

    static readonly HashSet<string> HistoryNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Rev", "System.ChangedDate", "System.ChangedBy", "System.AuthorizedDate", "System.RevisedDate", "System.AuthorizedAs",
        "System.PersonId", "System.Watermark", "System.CommentCount", "System.BoardColumnDone", "System.AreaId", "System.IterationId",
        "System.NodeName", "Microsoft.VSTS.Common.StateChangeDate", "System.ExternalLinkCount", "System.RelatedLinkCount",
        "System.HyperLinkCount", "System.AttachedFileCount", "System.RemoteLinkCount", "System.History", "System.Parent",
    };

    /// <summary>Who changed what and when; rich text changes are named, not dumped (detail=full shows the new text).</summary>
    public static string History(List<JsonElement> updates, Snapshot? s, string detail)
    {
        var lines = new List<string>();
        foreach (var u in updates)
        {
            var who = Json.Str(Json.Obj(u, "revisedBy"), "displayName");
            // revisedDate of the latest update is 9999-01-01; its System.ChangedDate (read below) is the real time.
            var when = Json.Str(u, "revisedDate");
            var changes = new List<string>();
            var fields = Json.Obj(u, "fields");
            string? NewValue(string reference) => fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(reference, out var f)
                ? Value(Json.Obj(f, "newValue")) : null;
            // The first revision sets every field: say what it was created as instead of listing them.
            var creation = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("System.CreatedDate", out _);
            if (creation)
                changes.Add($"created as {NewValue("System.WorkItemType")} [{NewValue("System.State")}]" +
                            (NewValue("System.AssignedTo") is { } a ? $", assigned {a}" : ""));
            if (fields.ValueKind == JsonValueKind.Object)
                foreach (var f in fields.EnumerateObject())
                {
                    if (f.Name == "System.ChangedDate") when = Json.Scalar(Json.Obj(f.Value, "newValue")) ?? when;
                    if (HistoryNoise.Contains(f.Name) || IsNoise(f.Name) && detail != "full") continue;
                    if (creation && detail != "full" && !f.Name.StartsWith("Custom.", StringComparison.Ordinal) && f.Name != "System.Tags") continue;
                    // The board column follows the state; repeat it only when it says something else.
                    if (f.Name == "System.BoardColumn" && string.Equals(NewValue(f.Name), NewValue("System.State"), StringComparison.OrdinalIgnoreCase)) continue;
                    var label = s?.Fields.GetValueOrDefault(f.Name)?.Name ?? f.Name;
                    var oldV = Value(Json.Obj(f.Value, "oldValue"));
                    var newV = Value(Json.Obj(f.Value, "newValue"));
                    if (s?.Fields.GetValueOrDefault(f.Name)?.Type == "html" || f.Name is WorkItems.DescriptionRef or WorkItems.AcceptanceRef or WorkItems.ReproStepsRef)
                    {
                        var before = Html.ToMarkdown(oldV);
                        var after = Html.ToMarkdown(newV);
                        changes.Add(detail == "full" ? $"{label}: \"{Formatting.Collapse(after, 500)}\"" : $"{label} {(before.Length == 0 ? "set" : after.Length == 0 ? "cleared" : "changed")}");
                    }
                    else changes.Add($"{label}: {(string.IsNullOrEmpty(oldV) ? "-" : Formatting.Collapse(oldV, 60))} -> {(string.IsNullOrEmpty(newV) ? "-" : Formatting.Collapse(newV, 60))}");
                }
            var rel = Json.Obj(u, "relations");
            foreach (var r in Json.Arr(rel, "added")) changes.Add("+" + RelationText(r));
            foreach (var r in Json.Arr(rel, "removed")) changes.Add("-" + RelationText(r));
            if (changes.Count == 0) continue;
            lines.Add($"{Date(when)} {who}: {string.Join("; ", changes)}");
        }
        if (lines.Count == 0) return "no recorded changes";
        var shown = detail == "full" || lines.Count <= 40 ? lines : lines.TakeLast(40).ToList();
        var head = shown.Count < lines.Count ? $"history (last {shown.Count} of {lines.Count} changes, detail=full for all):" : $"history ({lines.Count} changes):";
        return head + "\n" + string.Join('\n', shown);
    }

    static string? Value(JsonElement v) => v.ValueKind == JsonValueKind.Object ? Json.Str(v, "displayName") : Json.Scalar(v);

    static string RelationText(JsonElement r)
    {
        var rel = Json.Str(r, "rel") ?? "";
        var url = Json.Str(r, "url") ?? "";
        var attrs = Json.Obj(r, "attributes");
        if (rel == "ArtifactLink") return Artifact(url, Json.Str(attrs, "name"), null);
        var id = url[(url.LastIndexOf('/') + 1)..];
        return $"{RelationNames.GetValueOrDefault(rel) ?? Json.Str(attrs, "name") ?? rel} #{id}";
    }

    /// <summary>Query results: one line per item with the usual columns plus the query's own extra columns.</summary>
    public static string Table(List<JsonElement> items, IReadOnlyList<string> extraColumns, Snapshot? s, int total, string title)
    {
        if (items.Count == 0) return $"no work items ({title})";
        var sb = new StringBuilder($"{title}: {(total > items.Count ? $"{items.Count} of {total} (top=… shows more)" : items.Count.ToString(CultureInfo.InvariantCulture))}\n");
        foreach (var wi in items)
        {
            var project = WorkItems.Field(wi, "System.TeamProject");
            var parts = new List<string> { Header(wi) };
            if (WorkItems.Field(wi, "System.AssignedTo") is { } who) parts.Add(who);
            if (ShortPath(WorkItems.Field(wi, "System.IterationPath"), project) is { Length: > 0 } it && it != project) parts.Add(it);
            foreach (var col in extraColumns)
                if (WorkItems.Field(wi, col) is { Length: > 0 } v)
                    parts.Add($"{s?.Fields.GetValueOrDefault(col)?.Name ?? col}: {Formatting.Collapse(Html.ToMarkdown(v), 80)}");
            sb.AppendLine(string.Join(" | ", parts));
        }
        return sb.ToString().TrimEnd();
    }
}
