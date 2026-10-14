namespace AdoLens;

/// <summary>Backlog level of a work item type: what kind of content it holds, whatever the process calls it.</summary>
public static class Levels
{
    public const string Portfolio = "portfolio";
    public const string Requirement = "requirement";
    public const string Task = "task";
    public const string Bug = "bug";
    public const string Other = "other";
    public const string Hidden = "hidden";

    /// <summary>Display order.</summary>
    public static readonly string[] Order = [Portfolio, Requirement, Task, Bug, Other, Hidden];
}

public sealed record ProjectInfo(string Id, string Name, string? Process, bool Git, bool Tfvc, string? DefaultTeam, DateTime RefreshedUtc)
{
    public string SourceControl => (Git, Tfvc) switch
    {
        (true, true) => "Git + TFVC",
        (true, false) => "Git",
        (false, true) => "TFVC",
        _ => "no repositories",
    };
}

public sealed record StateInfo(string Name, string Category);

public sealed record TypeInfo(string Name, string Ref, string Level, string? Description, bool Disabled,
    List<StateInfo> States, Dictionary<string, List<string>> Transitions);

/// <summary>An organization-level field definition.</summary>
public sealed record FieldDef(string Ref, string Name, string Type, bool Picklist, bool ReadOnly, string? Description);

/// <summary>How a field behaves on one work item type.</summary>
public sealed record TypeField(string Ref, string Name, bool Required, string? Default, List<string> Allowed, string? Help);

public sealed record ChangeRecord(DateTime AtUtc, string Project, string? Type, string Kind, string? Ref, string Detail);

/// <summary>Everything ADO Lens knows about the structure of one project.</summary>
public sealed record Snapshot(ProjectInfo Project, List<TypeInfo> Types, Dictionary<string, List<TypeField>> FieldsByType,
    Dictionary<string, FieldDef> Fields)
{
    public TypeInfo? FindType(string name) =>
        Types.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? Types.FirstOrDefault(t => t.Ref.Equals(name, StringComparison.OrdinalIgnoreCase));

    public List<TypeField> FieldsOf(string type) =>
        FieldsByType.TryGetValue(type, out var list) ? list : [];

    /// <summary>
    /// What changed between two refreshes of the same project: types, states, fields, required flags and allowed values.
    /// </summary>
    public static List<ChangeRecord> Diff(Snapshot? old, Snapshot now)
    {
        var list = new List<ChangeRecord>();
        if (old is null) return list;
        var at = now.Project.RefreshedUtc;
        var p = now.Project.Name;
        void Add(string? type, string kind, string? field, string detail) => list.Add(new(at, p, type, kind, field, detail));

        var oldTypes = old.Types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var newTypes = now.Types.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var t in now.Types.Where(t => !oldTypes.ContainsKey(t.Name))) Add(t.Name, "type added", null, t.Level);
        foreach (var t in old.Types.Where(t => !newTypes.ContainsKey(t.Name))) Add(t.Name, "type removed", null, t.Level);

        foreach (var t in now.Types)
        {
            if (!oldTypes.TryGetValue(t.Name, out var ot)) continue;
            if (ot.Disabled != t.Disabled) Add(t.Name, t.Disabled ? "type disabled" : "type enabled", null, "");
            var oldStates = ot.States.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newStates = t.States.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var s in t.States.Where(s => !oldStates.Contains(s.Name))) Add(t.Name, "state added", null, $"{s.Name} [{s.Category}]");
            foreach (var s in ot.States.Where(s => !newStates.Contains(s.Name))) Add(t.Name, "state removed", null, s.Name);

            var of = old.FieldsOf(t.Name).ToDictionary(f => f.Ref, StringComparer.OrdinalIgnoreCase);
            var nf = now.FieldsOf(t.Name).ToDictionary(f => f.Ref, StringComparer.OrdinalIgnoreCase);
            foreach (var f in nf.Values.Where(f => !of.ContainsKey(f.Ref))) Add(t.Name, "field added", f.Ref, f.Name + (f.Required ? " (required)" : ""));
            foreach (var f in of.Values.Where(f => !nf.ContainsKey(f.Ref))) Add(t.Name, "field removed", f.Ref, f.Name);
            foreach (var f in nf.Values)
            {
                if (!of.TryGetValue(f.Ref, out var o)) continue;
                if (o.Required != f.Required) Add(t.Name, "field required", f.Ref, f.Required ? $"{f.Name}: now required" : $"{f.Name}: no longer required");
                var added = f.Allowed.Except(o.Allowed, StringComparer.Ordinal).ToList();
                var removed = o.Allowed.Except(f.Allowed, StringComparer.Ordinal).ToList();
                if (added.Count + removed.Count > 0)
                    Add(t.Name, "values changed", f.Ref,
                        f.Name + ": " + string.Join(", ", added.Select(v => "+" + v).Concat(removed.Select(v => "-" + v))));
            }
        }

        foreach (var (key, f) in now.Fields)
            if (old.Fields.TryGetValue(key, out var o) && o.Name != f.Name)
                Add(null, "field renamed", f.Ref, $"{o.Name} -> {f.Name}");
        return list;
    }
}
