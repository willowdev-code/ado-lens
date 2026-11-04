using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdoLens;

/// <summary>
/// One change ADO Lens made: a field update (values before and after), a created item, a comment or a link. Entries of
/// one tool call share a batch; ado_undo reverts the last batch.
/// </summary>
public sealed record AuditEntry
{
    public string Batch { get; init; } = "";
    public DateTime Utc { get; init; }
    /// <summary>update | create | comment | comment-edit | comment-delete | link-add | link-remove | undone (Batch: the batch reverted).</summary>
    public string Kind { get; init; } = "";
    public int Id { get; init; }
    public string? Project { get; init; }
    public string? Title { get; init; }
    public int? Rev { get; init; }
    public Dictionary<string, string?>? Before { get; init; }
    public Dictionary<string, string?>? After { get; init; }
    public int? CommentId { get; init; }
    /// <summary>A comment's text (HTML) before and after an edit; before a delete.</summary>
    public string? BeforeText { get; init; }
    public string? AfterText { get; init; }
    public string? Rel { get; init; }
    public int? Target { get; init; }
    public string? Tool { get; init; }
}

/// <summary>The local log of changes (%LOCALAPPDATA%\ado-lens\audit\&lt;connection&gt;.jsonl) and undo.</summary>
public static class Audit
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static readonly object Gate = new();

    public static string PathFor(Lens lens, string connection) => Path.Combine(lens.DataDir, "audit", connection + ".jsonl");

    public static string NewBatch() => Guid.NewGuid().ToString("N")[..12];

    public static void Write(Lens lens, string connection, IEnumerable<AuditEntry> entries)
    {
        var lines = entries.Select(e => JsonSerializer.Serialize(e with { Utc = e.Utc == default ? DateTime.UtcNow : e.Utc }, Options)).ToList();
        if (lines.Count == 0) return;
        var path = PathFor(lens, connection);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllLines(path, lines);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static List<AuditEntry> Read(Lens lens, string connection)
    {
        var path = PathFor(lens, connection);
        if (!File.Exists(path)) return [];
        var list = new List<AuditEntry>();
        lock (Gate)
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<AuditEntry>(line, Options) is { } e) list.Add(e); }
                catch (JsonException) { }
            }
        return list;
    }

    /// <summary>The value of a field as it can be written back: people by account, everything else as text.</summary>
    public static string? Raw(JsonElement wi, string reference)
    {
        var fields = Json.Obj(wi, "fields");
        if (fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty(reference, out var v)) return null;
        return v.ValueKind == JsonValueKind.Object ? Json.Str(v, "uniqueName") ?? Json.Str(v, "displayName") : Json.Scalar(v);
    }

    /// <summary>The audit entries of a JSON Patch written to an item (fields with before/after; parent links).</summary>
    public static List<AuditEntry> FromOps(string batch, string tool, JsonElement before, JsonElement after, IEnumerable<object> ops)
    {
        var id = WorkItems.Id(after.ValueKind == JsonValueKind.Object ? after : before);
        var project = WorkItems.Field(before, "System.TeamProject") ?? WorkItems.Field(after, "System.TeamProject");
        var title = WorkItems.Field(after, "System.Title") ?? WorkItems.Field(before, "System.Title");
        var b = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var a = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var list = new List<AuditEntry>();
        foreach (var op in ops.Select(o => JsonSerializer.SerializeToElement(o)))
        {
            var path = Json.Str(op, "path") ?? "";
            if (path.StartsWith("/fields/", StringComparison.Ordinal))
            {
                var reference = path[8..];
                b[reference] = Raw(before, reference);
                a[reference] = Raw(after, reference);
            }
            else if (path == "/relations/-" && op.TryGetProperty("value", out var rel))
                list.Add(new AuditEntry { Batch = batch, Tool = tool, Kind = "link-add", Id = id, Project = project, Title = title,
                    Rel = Json.Str(rel, "rel"), Target = TargetOf(Json.Str(rel, "url")) });
        }
        if (b.Count > 0)
            list.Insert(0, new AuditEntry { Batch = batch, Tool = tool, Kind = "update", Id = id, Project = project, Title = title,
                Rev = after.TryGetProperty("rev", out var r) && r.TryGetInt32(out var n) ? n : null, Before = b, After = a });
        return list;
    }

    public static int? TargetOf(string? url) =>
        url is not null && int.TryParse(url[(url.LastIndexOf('/') + 1)..], out var t) ? t : null;

    internal static string Token(string batch) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("undo:" + batch)))[..8].ToLowerInvariant();

    static string Describe(AuditEntry e) => e.Kind switch
    {
        "update" => $"#{e.Id} {e.Title}: " + string.Join("; ", e.Before!.Select(kv =>
            $"{kv.Key.Split('.')[^1]} {Formatting.Collapse(e.After!.GetValueOrDefault(kv.Key) ?? "-", 30)} -> {Formatting.Collapse(kv.Value ?? "-", 30)}")),
        "create" => $"#{e.Id} {e.Title}: created -> set to a removed state",
        "comment" => $"#{e.Id} {e.Title}: comment {e.CommentId} deleted",
        "comment-edit" => $"#{e.Id}: comment {e.CommentId} back to \"{Formatting.Collapse(Html.ToMarkdown(e.BeforeText), 60)}\"",
        "comment-delete" => $"#{e.Id}: deleted comment added back (as a new comment): \"{Formatting.Collapse(Html.ToMarkdown(e.BeforeText), 60)}\"",
        "link-add" => $"#{e.Id}: link {Short(e.Rel)} #{e.Target} removed",
        "link-remove" => $"#{e.Id}: link {Short(e.Rel)} #{e.Target} added back",
        _ => e.Kind,
    };

    static string Short(string? rel) => rel?.Split('.')[^1] ?? "?";

    /// <summary>The last changes ADO Lens made (latest first), numbered for ado_undo item=; with a work item id only its changes.</summary>
    public static string List(Lens lens, string? connection, int top, int? workItem = null)
    {
        var cfg = lens.For(connection).Config;
        var batches = Open(Read(lens, cfg.Connection));
        if (batches.Count == 0) return "no changes by ADO Lens to undo on this connection";
        var sb = new StringBuilder($"changes by ADO Lens on {cfg.Connection}{(workItem is { } w ? $" touching #{w}" : "")}, latest first " +
                                   "(ado_undo reverts the first; item=<n> another, id=<work item> the latest on that item):\n");
        var shown = 0;
        for (var i = 0; i < batches.Count && shown < Math.Clamp(top, 1, 20); i++)
        {
            var batch = batches[i];
            if (workItem is { } only && !batch.Any(e => e.Id == only)) continue;
            shown++;
            sb.AppendLine($"{i + 1}. {batch[0].Utc.ToLocalTime():yyyy-MM-dd HH:mm} {batch[0].Tool}: {string.Join(" | ", batch.Take(5).Select(Describe))}{(batch.Count > 5 ? $" | … {batch.Count - 5} more" : "")}");
        }
        if (shown == 0) sb.AppendLine($"- none touching #{workItem}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Batches not reverted yet, latest first.</summary>
    static List<List<AuditEntry>> Open(List<AuditEntry> all)
    {
        var undone = all.Where(e => e.Kind == "undone").Select(e => e.Batch).ToHashSet();
        return all.Where(e => e.Kind != "undone" && !undone.Contains(e.Batch))
            .GroupBy(e => e.Batch).Select(g => g.ToList()).OrderByDescending(g => g[0].Utc).ToList();
    }

    /// <summary>
    /// Reverts the last batch: always a preview first, written with its token. Fields someone changed after ADO Lens
    /// are left as they are and reported.
    /// </summary>
    public static async Task<string> UndoAsync(Lens lens, string? connection, string? confirm, CancellationToken ct = default, int? item = null, int? workItem = null)
    {
        var session = lens.For(connection);
        var batches = Open(Read(lens, session.Config.Connection));
        if (batches.Count == 0) return "nothing to undo: no changes by ADO Lens on this connection";
        // The token names its batch: a confirm reverts what its preview showed, whichever it was.
        var confirmed = confirm is { Length: > 0 } c ? batches.FirstOrDefault(b => string.Equals(Token(b[0].Batch), c.Trim(), StringComparison.OrdinalIgnoreCase)) : null;
        var index = confirmed is not null ? batches.IndexOf(confirmed)
            : item is { } nth ? nth - 1
            : workItem is { } w ? batches.FindIndex(b => b.Any(e => e.Id == w))
            : 0;
        if (index < 0 || index >= batches.Count)
            return item is not null ? $"no change {item} to undo (ado_undo list=true shows them)" : $"no change by ADO Lens on #{workItem} to undo";
        var batch = batches[index];
        var token = Token(batch[0].Batch);
        if (confirmed is null)
        {
            // An older change: say what came after it on the same items, so nothing newer is overwritten unseen.
            var touched = batch.Select(e => e.Id).ToHashSet();
            var later = batches.Take(index).Where(b => b.Any(e => touched.Contains(e.Id))).ToList();
            return $"UNDO PREVIEW - nothing written yet. {(index == 0 ? "The last change" : $"Change {index + 1}")} ({batch[0].Tool}, {batch[0].Utc.ToLocalTime():yyyy-MM-dd HH:mm}) would be reverted:\n" +
                   string.Join('\n', batch.Select(e => "- " + Describe(e))) +
                   (later.Count > 0 ? $"\nnote: {later.Count} later change(s) by ADO Lens touch the same item(s): " +
                                      string.Join(" | ", later.Select(b => $"{b[0].Utc.ToLocalTime():MM-dd HH:mm} {b[0].Tool}")) + " - fields changed since are left as they are" : "") +
                   $"\nShow this to the user. To revert it, call again with confirm={token}.";
        }

        var work = session.Work;
        var ids = batch.Select(e => e.Id).Distinct().ToList();
        var current = (await work.GetAsync(ids, relations: true, ct: ct)).ToDictionary(WorkItems.Id);
        var results = new List<string>();
        foreach (var e in batch.AsEnumerable().Reverse())
        {
            try
            {
                if (!current.TryGetValue(e.Id, out var wi)) { results.Add($"skipped #{e.Id}: not visible any more"); continue; }
                switch (e.Kind)
                {
                    case "update":
                        var rev = wi.TryGetProperty("rev", out var r) && r.TryGetInt32(out var n) ? n : 0;
                        var ops = new List<object>();
                        var kept = new List<string>();
                        foreach (var (reference, before) in e.Before!)
                        {
                            var now = Raw(wi, reference);
                            // Changed by someone else after ADO Lens: leave it.
                            if (rev != e.Rev && !Same(now, e.After!.GetValueOrDefault(reference))) { kept.Add(reference.Split('.')[^1]); continue; }
                            if (before is null) { if (now is not null) ops.Add(new { op = "remove", path = "/fields/" + reference }); }
                            else ops.Add(new { op = "add", path = "/fields/" + reference, value = before });
                        }
                        if (ops.Count > 0) await work.PatchAsync(e.Id, ops, ct);
                        results.Add($"reverted #{e.Id} {e.Title}" + (kept.Count > 0 ? $" (left as changed since by others: {string.Join(", ", kept)})" : ""));
                        break;
                    case "comment" when e.CommentId is null:
                        results.Add($"#{e.Id}: the comment id is unknown - delete it in Azure DevOps");
                        break;
                    case "comment":
                        await session.Client.SendAsync(HttpMethod.Delete,
                            $"{AdoClient.Scope(e.Project!)}_apis/wit/workItems/{e.Id}/comments/{e.CommentId}?api-version=7.1-preview.4", null, null, ct);
                        results.Add($"deleted the comment on #{e.Id}");
                        break;
                    case "comment-edit":
                    {
                        // Only when the comment still has the text ADO Lens wrote; an edit by someone since is kept.
                        var now = await work.CommentAsync(e.Project!, e.Id, e.CommentId!.Value, ct);
                        if (!Same(Html.ToMarkdown(Json.Str(now, "text")), Html.ToMarkdown(e.AfterText)))
                        { results.Add($"#{e.Id}: comment {e.CommentId} was changed since - left as it is"); break; }
                        await work.UpdateCommentAsync(e.Project!, e.Id, e.CommentId.Value, e.BeforeText ?? "", ct);
                        results.Add($"#{e.Id}: comment {e.CommentId} has its old text again");
                        break;
                    }
                    case "comment-delete":
                        var back = await work.AddCommentAsync(e.Project!, e.Id, e.BeforeText ?? "", ct);
                        results.Add($"#{e.Id}: the deleted comment is back as comment {(back.TryGetProperty("id", out var bid) ? bid.ToString() : "?")} (today's date)");
                        break;
                    case "create":
                        var s = (await session.Meta.GetAsync(e.Project!, ct)).Snapshot;
                        var type = s.FindType(WorkItems.Field(wi, "System.WorkItemType") ?? "");
                        var removed = type?.States.FirstOrDefault(x => x.Category == "Removed")?.Name;
                        if (removed is null) { results.Add($"#{e.Id} {e.Title}: its type has no removed state - delete it in Azure DevOps if needed"); break; }
                        await work.PatchAsync(e.Id, [new { op = "add", path = "/fields/System.State", value = removed }], ct);
                        results.Add($"#{e.Id} {e.Title}: set to {removed}");
                        break;
                    case "link-add":
                        var rels = Json.Arr(wi, "relations").ToList();
                        var i = rels.FindIndex(x => Json.Str(x, "rel") == e.Rel && TargetOf(Json.Str(x, "url")) == e.Target);
                        if (i >= 0) await work.PatchAsync(e.Id, [new { op = "remove", path = $"/relations/{i}" }], ct);
                        results.Add($"#{e.Id}: removed link {Short(e.Rel)} #{e.Target}");
                        break;
                    case "link-remove":
                        await work.PatchAsync(e.Id, [new { op = "add", path = "/relations/-", value = new { rel = e.Rel, url = $"{work.WebBase}_apis/wit/workItems/{e.Target}" } }], ct);
                        results.Add($"#{e.Id}: added back link {Short(e.Rel)} #{e.Target}");
                        break;
                }
            }
            catch (AdoApiException ex) { results.Add($"FAILED #{e.Id}: {ex.Message}"); }
        }
        Write(lens, session.Config.Connection, [new AuditEntry { Batch = batch[0].Batch, Kind = "undone", Tool = "ado_undo" }]);
        return "undone:\n" + string.Join('\n', results.Select(x => "- " + x));
    }

    static bool Same(string? a, string? b) =>
        string.Equals(Formatting.Collapse(a ?? "", 2000), Formatting.Collapse(b ?? "", 2000), StringComparison.OrdinalIgnoreCase);
}
