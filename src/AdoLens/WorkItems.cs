using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdoLens;

/// <summary>What a create or update should set. Text fields are markdown; any other field goes by name or reference name.</summary>
public sealed record WriteRequest
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? AcceptanceCriteria { get; init; }
    public string? State { get; init; }
    public string? Reason { get; init; }
    public string? AssignedTo { get; init; }
    public string? Area { get; init; }
    public string? Iteration { get; init; }
    /// <summary>"a; b" replaces the tags; "+a; -b" adds and removes.</summary>
    public string? Tags { get; init; }
    public Dictionary<string, string>? Fields { get; init; }
    public int? Parent { get; init; }
    /// <summary>On create: a work item template of the team whose values fill the fields not given.</summary>
    public string? Template { get; init; }

    public bool IsEmpty => Title is null && Description is null && AcceptanceCriteria is null && State is null && Reason is null
                           && AssignedTo is null && Area is null && Iteration is null && Tags is null && Parent is null
                           && (Fields is null || Fields.Count == 0);
}

/// <summary>Work items: batch reads, comments, history, queries, create and update with checks against the metadata.</summary>
public sealed class WorkItems(AdoClient client, MetadataService meta, Identities people, Config cfg)
{
    const int BatchSize = 200;
    const string CommentsApi = "api-version=7.1-preview.4";

    public const string TitleRef = "System.Title";
    public const string DescriptionRef = "System.Description";
    public const string AcceptanceRef = "Microsoft.VSTS.Common.AcceptanceCriteria";
    public const string ReproStepsRef = "Microsoft.VSTS.TCM.ReproSteps";

    public Config Config => cfg;
    public MetadataService Meta => meta;
    public string WebBase => $"https://dev.azure.com/{cfg.Organization}/";

    /// <summary>Work items by id, 200 per request; missing or inaccessible ids are left out.</summary>
    public async Task<List<JsonElement>> GetAsync(IReadOnlyList<int> ids, bool relations, IReadOnlyList<string>? fields = null, CancellationToken ct = default)
    {
        var tasks = ids.Distinct().Chunk(BatchSize).Select(chunk =>
        {
            var body = new Dictionary<string, object?> { ["ids"] = chunk, ["errorPolicy"] = "Omit" };
            if (relations) body["$expand"] = "Relations";
            else if (fields is { Count: > 0 }) body["fields"] = fields;
            return client.PostAsync("_apis/wit/workitemsbatch", body, ct);
        }).ToList();
        var list = new List<JsonElement>();
        foreach (var t in tasks)
            list.AddRange(Json.Arr(await t, "value").Where(v => v.ValueKind == JsonValueKind.Object));
        var order = ids.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
        return list.OrderBy(w => order.GetValueOrDefault(Id(w), int.MaxValue)).ToList();
    }

    /// <summary>The id of a work item or reference; 0 for null (e.g. "source": null in query relations).</summary>
    public static int Id(JsonElement wi) =>
        wi.ValueKind == JsonValueKind.Object && wi.TryGetProperty("id", out var id) && id.TryGetInt32(out var v) ? v : 0;

    public static string? Field(JsonElement wi, string reference)
    {
        var fields = Json.Obj(wi, "fields");
        if (fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty(reference, out var v)) return null;
        return v.ValueKind == JsonValueKind.Object ? Json.Str(v, "displayName") ?? Json.Str(v, "uniqueName") : Json.Scalar(v);
    }

    public async Task<List<JsonElement>> CommentsAsync(string project, int id, int top, CancellationToken ct = default)
    {
        var root = await client.GetAsync($"{AdoClient.Scope(project)}_apis/wit/workItems/{id}/comments?$top={top}&order=desc&{CommentsApi}", ct);
        var list = Json.Arr(root, "comments").ToList();
        list.Reverse();
        return list;
    }

    public async Task<List<JsonElement>> UpdatesAsync(string project, int id, CancellationToken ct = default)
    {
        var all = new List<JsonElement>();
        while (true)
        {
            var page = await client.GetListAsync($"{AdoClient.Scope(project)}_apis/wit/workItems/{id}/updates?$top=200&$skip={all.Count}", ct);
            all.AddRange(page);
            if (page.Count < 200 || all.Count >= 2000) return all;
        }
    }

    public async Task<string> TeamAsync(string project, CancellationToken ct = default) =>
        cfg.Team ?? (await meta.GetAsync(project, ct)).Snapshot.Project.DefaultTeam
        ?? throw new InvalidOperationException($"project {project} has no default team: run 'adolens config --connection {cfg.Connection} --team <name>'");

    /// <summary>Runs WIQL in the team's context (so @CurrentIteration works). Returns ids in query order and the query's columns.</summary>
    public async Task<(List<int> Ids, List<string> Columns)> WiqlAsync(string project, string wiql, int top, CancellationToken ct = default)
    {
        var team = await TeamAsync(project, ct);
        var root = await client.PostAsync($"{AdoClient.Scope(project)}{Uri.EscapeDataString(team)}/_apis/wit/wiql?$top={top}", new { query = wiql }, ct);
        return (QueryIds(root), Columns(root));
    }

    /// <summary>A saved query by id or path ("Shared Queries/Team/Open bugs", "My Queries/x").</summary>
    public async Task<(List<int> Ids, List<string> Columns, string Name)> SavedQueryAsync(string project, string pathOrId, int top, CancellationToken ct = default)
    {
        var scope = AdoClient.Scope(project);
        string id, name;
        if (Guid.TryParse(pathOrId, out _)) { id = pathOrId; name = pathOrId; }
        else
        {
            var path = string.Join('/', pathOrId.Trim('/').Split('/').Select(Uri.EscapeDataString));
            var q = await client.GetAsync($"{scope}_apis/wit/queries/{path}", ct);
            id = Json.Str(q, "id") ?? throw new InvalidOperationException($"no saved query '{pathOrId}'");
            name = Json.Str(q, "path") ?? pathOrId;
            if (Json.Bool(q, "isFolder")) throw new InvalidOperationException($"'{name}' is a folder, not a query");
        }
        var team = await TeamAsync(project, ct);
        var root = await client.GetAsync($"{scope}{Uri.EscapeDataString(team)}/_apis/wit/wiql/{id}?$top={top}", ct);
        return (QueryIds(root), Columns(root), name);
    }

    static List<int> QueryIds(JsonElement root)
    {
        var ids = Json.Arr(root, "workItems").Select(Id).ToList();
        // Tree and link queries answer with relations: their targets in order.
        if (ids.Count == 0)
            ids = Json.Arr(root, "workItemRelations").Select(r => Id(Json.Obj(r, "target"))).Where(i => i > 0).Distinct().ToList();
        return ids;
    }

    static List<string> Columns(JsonElement root) =>
        Json.Arr(root, "columns").Select(c => Json.Str(c, "referenceName")).Where(c => c is not null).Select(c => c!).ToList();

    public async Task<string?> CurrentIterationAsync(string project, CancellationToken ct = default)
    {
        var team = await TeamAsync(project, ct);
        var list = await client.GetListAsync($"{AdoClient.Scope(project)}{Uri.EscapeDataString(team)}/_apis/work/teamsettings/iterations?$timeframe=current", ct);
        return list.Select(i => Json.Str(i, "path")).FirstOrDefault();
    }

    public async Task<string?> NextIterationAsync(string project, CancellationToken ct = default)
    {
        var team = await TeamAsync(project, ct);
        var list = await client.GetListAsync($"{AdoClient.Scope(project)}{Uri.EscapeDataString(team)}/_apis/work/teamsettings/iterations?$timeframe=future", ct);
        return list.OrderBy(i => Json.Str(Json.Obj(i, "attributes"), "startDate") ?? "~").Select(i => Json.Str(i, "path")).FirstOrDefault();
    }

    public Task<JsonElement> CreateAsync(string project, string type, List<object> ops, CancellationToken ct = default) =>
        client.SendAsync(HttpMethod.Post, $"{AdoClient.Scope(project)}_apis/wit/workitems/${Uri.EscapeDataString(type)}", ops, "application/json-patch+json", ct);

    public Task<JsonElement> PatchAsync(int id, List<object> ops, CancellationToken ct = default) =>
        client.PatchAsync($"_apis/wit/workitems/{id}", ops, ct);

    public Task<JsonElement> AddCommentAsync(string project, int id, string html, CancellationToken ct = default) =>
        client.PostAsync($"{AdoClient.Scope(project)}_apis/wit/workItems/{id}/comments?{CommentsApi}", new { text = html }, ct);

    // ---- writing ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// JSON Patch operations for <paramref name="r"/> on a work item of type <paramref name="type"/>: field names are
    /// resolved against the type, values checked against allowed values, people resolved, markdown turned into HTML,
    /// and the state change checked against the process transitions. <paramref name="current"/> is null on create.
    /// </summary>
    public async Task<List<object>> BuildOpsAsync(Snapshot s, TypeInfo type, WriteRequest r, JsonElement? current, List<string> notes, CancellationToken ct = default)
    {
        var ops = new List<object>();
        var set = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var project = s.Project.Name;

        if (r.Title is not null) set[TitleRef] = r.Title.Trim();
        if (r.Description is not null)
        {
            // Bugs keep their story in "Repro Steps" when the type has it and no description field is shown.
            var target = Has(s, type, DescriptionRef) || !Has(s, type, ReproStepsRef) ? DescriptionRef : ReproStepsRef;
            set[target] = Html.FromMarkdown(r.Description);
        }
        if (r.AcceptanceCriteria is not null)
        {
            if (Has(s, type, AcceptanceRef)) set[AcceptanceRef] = Html.FromMarkdown(r.AcceptanceCriteria);
            else
            {
                // Types without the field (e.g. some CMMI or custom requirement types): the criteria become a section of the description.
                var description = r.Description ?? (current is { } c ? Html.ToMarkdown(Field(c, DescriptionRef)) : "");
                set[DescriptionRef] = Html.FromMarkdown(description.TrimEnd() + "\n\n# Acceptance criteria\n" + r.AcceptanceCriteria);
                notes.Add($"{type.Name} has no Acceptance Criteria field: the criteria were added to the description");
            }
        }
        if (r.AssignedTo is not null)
            set["System.AssignedTo"] = r.AssignedTo.Trim().Length == 0 ? "" : (await people.ResolveAsync(r.AssignedTo, ct)).FieldValue;
        if (r.Area is not null) set["System.AreaPath"] = TreePath(project, r.Area);
        if (r.Iteration is not null)
            set["System.IterationPath"] = r.Iteration.Trim().ToLowerInvariant() switch
            {
                "current" => await CurrentIterationAsync(project, ct) ?? throw new InvalidOperationException("the team has no current iteration"),
                "next" => await NextIterationAsync(project, ct) ?? throw new InvalidOperationException("the team has no future iteration"),
                _ => TreePath(project, r.Iteration),
            };
        if (r.Tags is not null) set["System.Tags"] = MergeTags(current is { } cur ? Field(cur, "System.Tags") : null, r.Tags);

        if (r.Fields is not null)
            foreach (var (key, value) in r.Fields)
            {
                var (tf, def) = FindField(s, type, key);
                set[tf.Ref] = await ConvertAsync(s, type, tf, def, value, ct);
            }

        if (r.State is not null)
        {
            var state = type.States.FirstOrDefault(x => x.Name.Equals(r.State.Trim(), StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new InvalidOperationException($"{type.Name} has no state '{r.State}'; states: {string.Join(", ", type.States.Select(x => x.Name))}");
            var from = current is { } c ? Field(c, "System.State") : null;
            if (from is not null && !from.Equals(state, StringComparison.OrdinalIgnoreCase)
                && type.Transitions.TryGetValue(from, out var allowed) && allowed.Count > 0
                && !allowed.Contains(state, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{type.Name} cannot go from {from} to {state}; allowed from {from}: {string.Join(", ", allowed)}");
            set["System.State"] = state;
        }
        if (r.Reason is not null) set["System.Reason"] = r.Reason.Trim();

        if (current is null)
        {
            // Fields Azure DevOps fills itself (ids from the paths, the creator, dates) are "required" in some processes
            // but cannot be given: never ask for them.
            var missing = s.FieldsOf(type.Name)
                .Where(f => f.Required && string.IsNullOrEmpty(f.Default) && !set.ContainsKey(f.Ref) && !FilledByAzureDevOps(f.Ref)
                            && !(s.Fields.TryGetValue(f.Ref, out var def) && def.ReadOnly))
                .Select(f => $"{f.Name} [{f.Ref}]" + (f.Allowed.Count > 0 ? $" ({string.Join(" | ", f.Allowed.Take(10))}{(f.Allowed.Count > 10 ? " | …" : "")})" : ""))
                .ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"{type.Name} needs: {string.Join("; ", missing)} - pass them in fields (by name or [reference]); area and iteration are fine as given");
        }

        foreach (var (reference, value) in set)
        {
            var has = current is { } c && Field(c, reference) is not null;
            if (value is string str && str.Length == 0)
            {
                if (has) ops.Add(new { op = "remove", path = "/fields/" + reference });
            }
            else ops.Add(new { op = "add", path = "/fields/" + reference, value });
        }
        if (r.Parent is { } parent)
            ops.Add(new { op = "add", path = "/relations/-", value = new { rel = "System.LinkTypes.Hierarchy-Reverse", url = $"{WebBase}_apis/wit/workItems/{parent}" } });

        Lengths(type, r, notes, Has(s, type, AcceptanceRef));
        return ops;
    }

    /// <summary>Fields set by Azure DevOps on every save, never by the caller.</summary>
    internal static bool FilledByAzureDevOps(string reference) =>
        reference is "System.State" or "System.Reason" or "System.AreaPath" or "System.IterationPath" or "System.AreaId" or "System.IterationId"
            or "System.Id" or "System.Rev" or "System.TeamProject" or "System.WorkItemType" or "System.CreatedBy" or "System.CreatedDate"
            or "System.ChangedBy" or "System.ChangedDate" or "System.AuthorizedAs" or "System.AuthorizedDate" or "System.RevisedDate"
            or "System.Watermark" or "System.NodeName" or "System.PersonId" or "System.CommentCount"
        || reference.StartsWith("System.AreaLevel", StringComparison.Ordinal) || reference.StartsWith("System.IterationLevel", StringComparison.Ordinal);

    static bool Has(Snapshot s, TypeInfo type, string reference) =>
        s.FieldsOf(type.Name).Any(f => f.Ref.Equals(reference, StringComparison.OrdinalIgnoreCase));

    /// <summary>A field of the type by reference name or display name.</summary>
    internal static (TypeField Field, FieldDef? Def) FindField(Snapshot s, TypeInfo type, string key)
    {
        var fields = s.FieldsOf(type.Name);
        var k = key.Trim();
        var tf = fields.FirstOrDefault(f => f.Ref.Equals(k, StringComparison.OrdinalIgnoreCase))
                 ?? fields.FirstOrDefault(f => f.Name.Equals(k, StringComparison.OrdinalIgnoreCase));
        if (tf is null)
        {
            var near = fields.Where(f => f.Name.Contains(k, StringComparison.OrdinalIgnoreCase) || f.Ref.Contains(k, StringComparison.OrdinalIgnoreCase))
                .Select(f => $"{f.Name} ({f.Ref})").Take(6).ToList();
            throw new InvalidOperationException($"{type.Name} has no field '{key}'" +
                (near.Count > 0 ? $"; similar: {string.Join(", ", near)}" : "; ado_fields type=… lists the fields"));
        }
        return (tf, s.Fields.GetValueOrDefault(tf.Ref));
    }

    async Task<object?> ConvertAsync(Snapshot s, TypeInfo type, TypeField tf, FieldDef? def, string value, CancellationToken ct)
    {
        var v = value.Trim();
        if (v.Length == 0) return "";
        if (def?.ReadOnly == true) throw new InvalidOperationException($"{tf.Name} is read-only");
        if (tf.Allowed.Count > 0)
        {
            var exact = tf.Allowed.FirstOrDefault(a => a.Equals(v, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
            var near = tf.Allowed.Where(a => a.StartsWith(v, StringComparison.OrdinalIgnoreCase) || a.Contains(" - " + v, StringComparison.OrdinalIgnoreCase)).ToList();
            if (near.Count == 1) return near[0];
            throw new InvalidOperationException(near.Count > 1
                ? $"'{value}' matches several values of {tf.Name}: {string.Join(" | ", near.Take(10))} - give the whole value"
                : $"'{value}' is not allowed in {tf.Name} of {type.Name}; values: {string.Join(" | ", tf.Allowed.Take(30))}");
        }
        switch (def?.Type)
        {
            case "integer":
                return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l
                    : throw new InvalidOperationException($"{tf.Name} takes a whole number, not '{value}'");
            case "double":
                return double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
                    : throw new InvalidOperationException($"{tf.Name} takes a number, not '{value}'");
            case "boolean":
                return v.ToLowerInvariant() is "true" or "yes" or "1" or "tak";
            case "identity":
                return (await people.ResolveAsync(v, ct)).FieldValue;
            case "html":
                return v.StartsWith('<') ? v : Html.FromMarkdown(value);
            case "treePath":
                return TreePath(s.Project.Name, v);
            default:
                return value;
        }
    }

    /// <summary>"Team\Sub" → "Project\Team\Sub"; a full path is kept.</summary>
    static string TreePath(string project, string path)
    {
        var p = path.Trim().Replace('/', '\\').Trim('\\');
        return p.StartsWith(project, StringComparison.OrdinalIgnoreCase) ? p : project + "\\" + p;
    }

    internal static string MergeTags(string? current, string change)
    {
        var parts = change.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!parts.Any(p => p.StartsWith('+') || p.StartsWith('-'))) return string.Join("; ", parts);
        var tags = (current ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var p in parts)
        {
            var name = p.TrimStart('+', '-').Trim();
            if (p.StartsWith('-')) tags.RemoveAll(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
            else if (!tags.Contains(name, StringComparer.OrdinalIgnoreCase)) tags.Add(name);
        }
        return string.Join("; ", tags);
    }

    /// <summary>Soft limits of the writing rules: informative notes, the write still happens.</summary>
    static void Lengths(TypeInfo type, WriteRequest r, List<string> notes, bool criteriaField)
    {
        var limit = type.Level switch { Levels.Requirement => 120, Levels.Bug => 100, Levels.Task => 40, Levels.Portfolio => 150, _ => 0 };
        // Without a criteria field the criteria are part of the description, so it may be longer.
        if (!criteriaField) limit *= 2;
        var words = Html.Words(r.Description) + (criteriaField ? 0 : Html.Words(r.AcceptanceCriteria));
        if (limit > 0 && words > limit)
            notes.Add($"description has {words} words; {type.Level} items usually need ≤ {limit} - what to do, not how");
        var criteria = (r.AcceptanceCriteria ?? "").Split('\n').Count(l => l.TrimStart() is var t && (t.StartsWith("- ") || t.StartsWith("* ") || (t.Length > 2 && char.IsDigit(t[0]) && t.Contains(". "))));
        if (criteria > 7) notes.Add($"{criteria} acceptance criteria; more than 7 usually means the item should be split");
        if (criteriaField && type.Level == Levels.Requirement && r.Description is not null && r.AcceptanceCriteria is null && words > 0)
            notes.Add("no acceptance criteria given");
    }

    // ---- bulk ------------------------------------------------------------------------------------------------------

    /// <summary>Stable short token for a preview: the same ids and request give the same token.</summary>
    public static string Token(IEnumerable<int> ids, WriteRequest r, string? comment)
    {
        var text = string.Join(',', ids.Order()) + "|" + JsonSerializer.Serialize(r) + "|" + comment;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..10].ToLowerInvariant();
    }
}
