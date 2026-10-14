using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>The team's work item templates (default values) and the process rules of a type.</summary>
public static partial class Templates
{
    static string Scope(string project, string team) => $"{AdoClient.Scope(project)}{Uri.EscapeDataString(team)}/";

    /// <summary>Templates of the team for a type: id and name.</summary>
    public static async Task<List<(string Id, string Name)>> ListAsync(Session session, string project, string type, CancellationToken ct)
    {
        var team = await session.Work.TeamAsync(project, ct);
        try
        {
            var list = await session.Client.GetListAsync($"{Scope(project, team)}_apis/wit/templates?workitemtypename={Uri.EscapeDataString(type)}", ct);
            return list.Select(t => (Json.Str(t, "id") ?? "", Json.Str(t, "name") ?? "")).Where(t => t.Item1.Length > 0).ToList();
        }
        catch (AdoApiException) { return []; }
    }

    /// <summary>The field values of a template of the team, by its name (or id).</summary>
    public static async Task<Dictionary<string, string>> FieldsAsync(Session session, string project, string type, string template, CancellationToken ct)
    {
        var list = await ListAsync(session, project, type, ct);
        var hit = list.FirstOrDefault(t => t.Name.Equals(template.Trim(), StringComparison.OrdinalIgnoreCase) || t.Id.Equals(template.Trim(), StringComparison.OrdinalIgnoreCase));
        if (hit.Id is null)
            throw new InvalidOperationException($"no template '{template}' for {type}; templates: {(list.Count == 0 ? "none" : string.Join(", ", list.Select(t => t.Name)))}");
        var team = await session.Work.TeamAsync(project, ct);
        var t = await session.Client.GetAsync($"{Scope(project, team)}_apis/wit/templates/{hit.Id}", ct);
        var fields = Json.Obj(t, "fields");
        return fields.ValueKind == JsonValueKind.Object
            ? fields.EnumerateObject().ToDictionary(p => p.Name, p => Json.Scalar(p.Value) ?? "", StringComparer.OrdinalIgnoreCase)
            : [];
    }

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelBreak();

    static string Words(string? camel) => camel is null ? "" : CamelBreak().Replace(camel, " ").ToLowerInvariant();

    /// <summary>Custom rules of the type in an inherited process, one line each ("when State = Done: make required Effort").</summary>
    public static async Task<List<string>> RulesAsync(Session session, Snapshot s, TypeInfo type, CancellationToken ct)
    {
        try
        {
            var props = await session.Client.GetListAsync($"_apis/projects/{s.Project.Id}/properties", ct);
            var processId = props.Where(p => Json.Str(p, "name") == "System.ProcessTemplateType").Select(p => Json.Str(p, "value")).FirstOrDefault();
            if (processId is null) return [];
            var rules = await session.Client.GetListAsync($"_apis/work/processes/{processId}/workItemTypes/{Uri.EscapeDataString(type.Ref)}/rules", ct);
            string Name(string? reference) =>
                reference is null ? "" : s.Fields.Values.FirstOrDefault(f => f.Ref.Equals(reference, StringComparison.OrdinalIgnoreCase))?.Name ?? reference;
            return rules.Where(r => !Json.Bool(r, "isDisabled") && Json.Str(r, "customizationType") != "system").Select(r =>
            {
                static string Part(params string?[] words) => string.Join(' ', words.Where(w => !string.IsNullOrWhiteSpace(w)));
                var when = string.Join(" and ", Json.Arr(r, "conditions").Select(c =>
                    Part(Words(Json.Str(c, "conditionType")), Name(Json.Str(c, "field")), Json.Str(c, "value") is { Length: > 0 } v ? $"= {v}" : null)));
                var then = string.Join(", ", Json.Arr(r, "actions").Select(a =>
                    Part(Words(Json.Str(a, "actionType")), Name(Json.Str(a, "targetField")), Json.Str(a, "value") is { Length: > 0 } v ? $"= {Formatting.Collapse(v, 40)}" : null)));
                return $"{(when.Length > 0 ? when : "always")}: {then}";
            }).ToList();
        }
        catch (AdoApiException) { return []; }
    }

    /// <summary>Templates and rules of a type for the ado_fields answer; empty when there are none.</summary>
    public static async Task<string> SectionAsync(Session session, Snapshot s, TypeInfo type, CancellationToken ct)
    {
        var templates = ListAsync(session, s.Project.Name, type.Name, ct);
        var rules = await RulesAsync(session, s, type, ct);
        var sb = new StringBuilder();
        if ((await templates) is { Count: > 0 } list) sb.AppendLine($"team templates: {string.Join(", ", list.Select(t => t.Name))} (ado_create template=<name>)");
        if (rules.Count > 0)
        {
            sb.AppendLine($"rules ({rules.Count}):");
            foreach (var r in rules.Take(20)) sb.AppendLine("- " + r);
            if (rules.Count > 20) sb.AppendLine($"- … {rules.Count - 20} more");
        }
        return sb.ToString().TrimEnd();
    }
}
