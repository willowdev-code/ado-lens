using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>A child work item a recipe creates under each item found.</summary>
public sealed record RecipeChild
{
    public string Type { get; init; } = "Task";
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? AssignedTo { get; init; }
    public Dictionary<string, string>? Fields { get; init; }
}

/// <summary>One step: which items (WIQL or ids) and what to do with each: change fields, comment, create a child.</summary>
public sealed record RecipeStep
{
    public string? Query { get; init; }
    public string? Ids { get; init; }
    public string? State { get; init; }
    public string? Reason { get; init; }
    public string? AssignedTo { get; init; }
    /// <summary>An iteration path, "current" or "next".</summary>
    public string? Iteration { get; init; }
    public string? Area { get; init; }
    public string? Tags { get; init; }
    public Dictionary<string, string>? Fields { get; init; }
    public string? Comment { get; init; }
    public RecipeChild? Child { get; init; }

    internal WriteRequest Request(Func<string?, string?> fill) => new()
    {
        State = fill(State), Reason = fill(Reason), AssignedTo = fill(AssignedTo), Iteration = fill(Iteration), Area = fill(Area), Tags = fill(Tags),
        Fields = Fields?.ToDictionary(kv => kv.Key, kv => fill(kv.Value) ?? ""),
    };
}

/// <summary>A named, repeatable change: steps with {parameters}, run from Claude or from the command line.</summary>
public sealed record Recipe
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? Project { get; init; }
    public List<string> Parameters { get; init; } = [];
    public List<RecipeStep> Steps { get; init; } = [];
}

/// <summary>Recipes saved locally (%LOCALAPPDATA%\ado-lens\recipes\&lt;name&gt;.json); every run is previewed first.</summary>
public static partial class Recipes
{
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,48}$")]
    private static partial Regex ValidName();

    public static string Dir(Lens lens) => Path.Combine(lens.DataDir, "recipes");

    static string PathOf(Lens lens, string name) => Path.Combine(Dir(lens), Normalize(name) + ".json");

    static string Normalize(string name)
    {
        var n = Regex.Replace(name.Trim().ToLowerInvariant(), @"[\s_]+", "-");
        return ValidName().IsMatch(n) ? n : throw new ArgumentException($"recipe name '{name}': letters, digits and '-' only (e.g. close-sprint)");
    }

    public static Recipe Load(Lens lens, string name)
    {
        var path = PathOf(lens, name);
        if (!File.Exists(path)) throw new InvalidOperationException($"no recipe '{name}'; saved: {string.Join(", ", Names(lens).DefaultIfEmpty("none"))}");
        return JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), Options) ?? throw new InvalidOperationException($"recipe '{name}' is empty");
    }

    static List<string> Names(Lens lens) =>
        Directory.Exists(Dir(lens)) ? Directory.GetFiles(Dir(lens), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToList() : [];

    public static string List(Lens lens)
    {
        var names = Names(lens);
        if (names.Count == 0) return $"no recipes yet ({Dir(lens)}); save one with action=save";
        var sb = new StringBuilder($"recipes ({Dir(lens)}):\n");
        foreach (var n in names)
        {
            try
            {
                var r = Load(lens, n);
                sb.AppendLine($"- {n}{(r.Parameters.Count > 0 ? $" ({string.Join(", ", r.Parameters)})" : "")}: {r.Description ?? $"{r.Steps.Count} steps"}");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { sb.AppendLine($"- {n}: unreadable ({ex.Message})"); }
        }
        return sb.ToString().TrimEnd();
    }

    public static string Show(Lens lens, string name) => $"{PathOf(lens, name)}\n{JsonSerializer.Serialize(Load(lens, name), Options)}";

    public static string Save(Lens lens, string name, string json)
    {
        Recipe recipe;
        try { recipe = JsonSerializer.Deserialize<Recipe>(json, Options) ?? throw new ArgumentException("recipe is empty"); }
        catch (JsonException ex) { throw new ArgumentException($"recipe is not valid JSON: {ex.Message}"); }
        recipe = recipe with { Name = Normalize(name) };
        if (recipe.Steps.Count == 0) throw new ArgumentException("a recipe needs at least one step");
        foreach (var (step, i) in recipe.Steps.Select((x, i) => (x, i + 1)))
        {
            if (step.Query is null == (step.Ids is null)) throw new ArgumentException($"step {i}: give either query (WIQL) or ids");
            if (step.Request(x => x).IsEmpty && step.Comment is null && step.Child is null) throw new ArgumentException($"step {i}: nothing to do");
            if (step.Child is { } c && string.IsNullOrWhiteSpace(c.Title)) throw new ArgumentException($"step {i}: child needs a title");
        }
        var used = Placeholder().Matches(json).Select(m => m.Groups[1].Value).Where(p => p is not ("id" or "title")).Distinct().ToList();
        var undeclared = used.Except(recipe.Parameters, StringComparer.OrdinalIgnoreCase).ToList();
        if (undeclared.Count > 0) recipe = recipe with { Parameters = [.. recipe.Parameters, .. undeclared] };
        Directory.CreateDirectory(Dir(lens));
        var existed = File.Exists(PathOf(lens, recipe.Name));
        File.WriteAllText(PathOf(lens, recipe.Name), JsonSerializer.Serialize(recipe, Options));
        return $"{(existed ? "replaced" : "saved")} recipe {recipe.Name}: {recipe.Steps.Count} steps" +
               (recipe.Parameters.Count > 0 ? $", parameters {string.Join(", ", recipe.Parameters)}" : "") +
               $"\nrun it with action=run (preview first), or from a terminal: adolens recipe run {recipe.Name}";
    }

    public static string Delete(Lens lens, string name)
    {
        var path = PathOf(lens, name);
        if (!File.Exists(path)) return $"no recipe '{name}'";
        File.Delete(path);
        return $"deleted recipe {Normalize(name)}";
    }

    static string Fill(string? text, IReadOnlyDictionary<string, string> values) =>
        text is null ? "" : Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    /// <summary>
    /// Runs a recipe: the first call (or no confirm) previews every step - how many items, which ones, what changes;
    /// confirm=&lt;token&gt; writes. <paramref name="yes"/> skips the preview (scheduled runs from the command line).
    /// </summary>
    public static async Task<string> RunAsync(Lens lens, string? connection, string name, IReadOnlyDictionary<string, string>? parameters,
        string? confirm, bool yes, CancellationToken ct = default)
    {
        var recipe = Load(lens, name);
        var values = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        var missing = recipe.Parameters.Where(p => !values.ContainsKey(p)).ToList();
        if (missing.Count > 0) throw new ArgumentException($"recipe {recipe.Name} needs: {string.Join(", ", missing)}");
        var session = lens.For(connection);
        var project = session.Project(recipe.Project);

        var plans = new List<(RecipeStep Step, List<int> Ids)>();
        foreach (var step in recipe.Steps)
        {
            var ids = step.Ids is not null ? WorkQueries.ParseIds(Fill(step.Ids, values))
                : (await session.Work.WiqlAsync(project, Fill(step.Query, values), 500, ct)).Ids;
            plans.Add((step, ids));
        }
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(recipe, Options) + string.Join(';', values.OrderBy(v => v.Key).Select(v => $"{v.Key}={v.Value}")) +
            string.Join('|', plans.Select(p => string.Join(',', p.Ids))))))[..8].ToLowerInvariant();

        if (!yes && !string.Equals(confirm?.Trim(), token, StringComparison.OrdinalIgnoreCase))
        {
            var heads = plans.SelectMany(p => p.Ids).Distinct().Take(200).ToList();
            var known = heads.Count == 0 ? [] : (await session.Work.GetAsync(heads, relations: false,
                ["System.Id", "System.WorkItemType", "System.State", "System.Title"], ct)).ToDictionary(WorkItems.Id, WorkItemFormat.Header);
            var sb = new StringBuilder($"PREVIEW of recipe {recipe.Name} - nothing written yet.\n");
            foreach (var ((step, ids), i) in plans.Select((p, i) => (p, i + 1)))
            {
                sb.AppendLine($"step {i}: {Describe(step, values)} -> {ids.Count} items");
                foreach (var id in ids.Take(10)) sb.AppendLine($"  - {known.GetValueOrDefault(id, $"#{id}")}");
                if (ids.Count > 10) sb.AppendLine($"  - … {ids.Count - 10} more");
            }
            return sb.Append($"Show this to the user. To run it, call again with confirm={token}.").ToString();
        }

        var result = new StringBuilder($"recipe {recipe.Name}:\n");
        foreach (var ((step, ids), i) in plans.Select((p, i) => (p, i + 1)))
        {
            if (ids.Count == 0) { result.AppendLine($"step {i}: no items"); continue; }
            string Text(string? x) => Fill(x, values);
            var request = step.Request(x => x is null ? null : Text(x));
            if (!request.IsEmpty || step.Comment is not null)
            {
                var text = await WorkQueries.UpdateAsync(lens, connection, string.Join(';', ids), null, request,
                    step.Comment is null ? null : Text(step.Comment), null, int.MaxValue, ct);
                result.AppendLine($"step {i}: {text.Split('\n')[0]}");
            }
            if (step.Child is { } child)
            {
                var items = await session.Work.GetAsync(ids, relations: false, ["System.Id", "System.Title", "System.TeamProject"], ct);
                var created = 0;
                foreach (var wi in items)
                {
                    var each = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
                    {
                        ["id"] = WorkItems.Id(wi).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["title"] = WorkItems.Field(wi, "System.Title") ?? "",
                    };
                    await WorkQueries.CreateAsync(lens, connection, WorkItems.Field(wi, "System.TeamProject") ?? project, child.Type, new WriteRequest
                    {
                        Title = Fill(child.Title, each), Description = child.Description is null ? null : Fill(child.Description, each),
                        AssignedTo = child.AssignedTo is null ? null : Fill(child.AssignedTo, each), Parent = WorkItems.Id(wi),
                        Fields = child.Fields?.ToDictionary(kv => kv.Key, kv => Fill(kv.Value, each)),
                    }, ct);
                    created++;
                }
                result.AppendLine($"step {i}: created {created} {child.Type} item(s)");
            }
        }
        return result.Append("ado_undo reverts the last step if needed.").ToString();
    }

    static string Describe(RecipeStep s, IReadOnlyDictionary<string, string> v)
    {
        var parts = new List<string>();
        if (s.State is not null) parts.Add($"state={Fill(s.State, v)}");
        if (s.Iteration is not null) parts.Add($"iteration={Fill(s.Iteration, v)}");
        if (s.AssignedTo is not null) parts.Add($"assigned to={Fill(s.AssignedTo, v)}");
        if (s.Area is not null) parts.Add($"area={Fill(s.Area, v)}");
        if (s.Tags is not null) parts.Add($"tags {Fill(s.Tags, v)}");
        if (s.Reason is not null) parts.Add($"reason={Fill(s.Reason, v)}");
        if (s.Fields is not null) parts.AddRange(s.Fields.Select(kv => $"{kv.Key}={Fill(kv.Value, v)}"));
        if (s.Comment is not null) parts.Add("comment");
        if (s.Child is { } c) parts.Add($"new {c.Type} '{c.Title}' under each");
        return string.Join(", ", parts);
    }
}
