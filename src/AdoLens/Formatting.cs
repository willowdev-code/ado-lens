using System.Globalization;
using System.Text;

namespace AdoLens;

/// <summary>Compact text answers: no JSON, no empty values, details only on request.</summary>
public static class Formatting
{
    /// <summary>Allowed values shown per field before "… N more".</summary>
    const int ValuesShown = 12;

    /// <summary>System fields always worth showing; the rest of System.* (ids, dates, counters, levels) is hidden by default.</summary>
    static readonly HashSet<string> UsefulSystemFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Title", "System.Description", "System.State", "System.Reason", "System.AssignedTo",
        "System.AreaPath", "System.IterationPath", "System.Tags", "System.History",
    };

    public static string Age(DateTime utc, DateTime? now = null)
    {
        var d = (now ?? DateTime.UtcNow) - utc;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(2)) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }

    static string Header(Snapshot s, bool refreshing) =>
        $"{s.Project.Name} - process {s.Project.Process ?? "(name not readable with this PAT)"}, {s.Project.SourceControl}; " +
        $"metadata {Age(s.Project.RefreshedUtc)}{(refreshing ? " (refreshing in background)" : "")}";

    /// <summary>Work item types grouped by backlog level.</summary>
    public static string Types(Snapshot s, bool refreshing)
    {
        var sb = new StringBuilder().AppendLine(Header(s, refreshing));
        foreach (var level in Levels.Order)
        {
            var names = s.Types.Where(t => t.Level == level && !t.Disabled).Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0) continue;
            sb.AppendLine($"{level}{(names.Count > 3 ? $" ({names.Count})" : "")}: {string.Join(", ", names)}");
        }
        var disabled = s.Types.Where(t => t.Disabled).Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (disabled.Count > 0) sb.AppendLine($"disabled: {string.Join(", ", disabled)}");
        sb.Append("type=<name> shows states, transitions and fields; field=<name> shows one field everywhere.");
        return sb.ToString();
    }

    /// <summary>States, transitions and fields of one type. Without <paramref name="all"/> housekeeping fields are left out.</summary>
    public static string Type(Snapshot s, TypeInfo t, bool all, bool refreshing)
    {
        var sb = new StringBuilder().AppendLine(Header(s, refreshing));
        sb.AppendLine($"{t.Name} ({t.Ref}) - level {t.Level}{(t.Disabled ? ", DISABLED" : "")}");
        if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine(Collapse(t.Description, 200));

        sb.AppendLine("states: " + string.Join(", ", t.States.Select(st => $"{st.Name} [{st.Category}]")));
        var transitions = t.Transitions.Where(kv => kv.Key.Length > 0 && kv.Value.Count > 0).ToList();
        if (transitions.Count > 0)
            sb.AppendLine("transitions: " + string.Join("; ", transitions.Select(kv => $"{kv.Key} -> {string.Join(", ", kv.Value)}")));

        var fields = s.FieldsOf(t.Name);
        var shown = fields.Where(f => all || IsUseful(s, f)).ToList();
        sb.AppendLine($"fields ({shown.Count}{(shown.Count < fields.Count ? $" of {fields.Count}; all=true lists ids, dates, counters and board fields" : "")}):");
        foreach (var f in shown.OrderByDescending(f => f.Required).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine("- " + FieldLine(s, f));
        return sb.ToString().TrimEnd();
    }

    static bool IsUseful(Snapshot s, TypeField f)
    {
        if (f.Required) return true;
        if (f.Ref.StartsWith("WEF_", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Fields.TryGetValue(f.Ref, out var def) && def.ReadOnly) return false;
        if (f.Ref.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) return UsefulSystemFields.Contains(f.Ref);
        return !ServerMaintainedFields.Contains(f.Ref);
    }

    /// <summary>Set by Azure DevOps on state changes; nobody edits them by hand.</summary>
    static readonly HashSet<string> ServerMaintainedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.VSTS.Common.StateChangeDate", "Microsoft.VSTS.Common.ActivatedDate", "Microsoft.VSTS.Common.ActivatedBy",
        "Microsoft.VSTS.Common.ResolvedDate", "Microsoft.VSTS.Common.ResolvedBy", "Microsoft.VSTS.Common.ClosedDate",
        "Microsoft.VSTS.Common.ClosedBy",
    };

    static string FieldLine(Snapshot s, TypeField f)
    {
        var def = s.Fields.GetValueOrDefault(f.Ref);
        var parts = new List<string> { $"{f.Name} ({f.Ref})", TypeName(def) };
        if (f.Required) parts.Add("required");
        if (def?.ReadOnly == true) parts.Add("read-only");
        if (!string.IsNullOrEmpty(f.Default)) parts.Add($"default {f.Default}");
        var line = string.Join(", ", parts);
        if (f.Allowed.Count > 0) line += ": " + Values(f.Allowed, ValuesShown);
        return line;
    }

    static string TypeName(FieldDef? def) => def is null ? "?" : def.Picklist ? $"{def.Type} list" : def.Type switch
    {
        "plainText" => "text",
        "html" => "rich text",
        "treePath" => "path",
        _ => def.Type,
    };

    static string Values(List<string> values, int max) =>
        values.Count <= max ? string.Join(" | ", values) : string.Join(" | ", values.Take(max)) + $" | … {values.Count - max} more (field=<name> lists all)";

    /// <summary>
    /// Fields matching a name or reference name (wildcards * ?), with the types that use them, required flags and all values.
    /// </summary>
    public static string Field(Snapshot s, string pattern, bool refreshing)
    {
        var rx = Wildcard(pattern);
        var defs = s.Fields.Values.Where(f => rx.IsMatch(f.Name) || rx.IsMatch(f.Ref))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (defs.Count == 0)
            return $"no field matches '{pattern}' in {s.Project.Name} (metadata {Age(s.Project.RefreshedUtc)}; refresh=true fetches it again)";

        var sb = new StringBuilder().AppendLine(Header(s, refreshing));
        const int MaxFields = 15;
        foreach (var def in defs.Take(MaxFields))
        {
            sb.AppendLine($"{def.Name} ({def.Ref}) - {TypeName(def)}{(def.ReadOnly ? ", read-only" : "")}");
            if (!string.IsNullOrWhiteSpace(def.Description)) sb.AppendLine("  " + Collapse(def.Description, 200));
            var uses = s.Types.Where(t => !t.Disabled)
                .Select(t => (Type: t, Field: s.FieldsOf(t.Name).FirstOrDefault(f => f.Ref.Equals(def.Ref, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.Field is not null).ToList();
            if (uses.Count == 0) { sb.AppendLine("  used by no work item type of this project"); continue; }
            var required = uses.Where(u => u.Field!.Required).Select(u => u.Type.Name).ToList();
            sb.AppendLine($"  used by {uses.Count} type(s): {string.Join(", ", uses.Select(u => u.Type.Name).Take(20))}{(uses.Count > 20 ? ", …" : "")}");
            if (required.Count > 0) sb.AppendLine($"  required in: {string.Join(", ", required)}");
            // Allowed values can differ per type; group identical lists.
            foreach (var g in uses.Where(u => u.Field!.Allowed.Count > 0).GroupBy(u => string.Join("\u0001", u.Field!.Allowed)))
            {
                var values = g.First().Field!.Allowed;
                var where = uses.Count(u => u.Field!.Allowed.Count > 0) == g.Count() ? "" : $" ({string.Join(", ", g.Select(u => u.Type.Name))})";
                sb.AppendLine($"  values{where}: {Values(values, 200)}");
            }
            var defaults = uses.Where(u => !string.IsNullOrEmpty(u.Field!.Default)).Select(u => u.Field!.Default).Distinct().ToList();
            if (defaults.Count > 0) sb.AppendLine($"  default: {string.Join(" / ", defaults)}");
        }
        if (defs.Count > MaxFields) sb.AppendLine($"… {defs.Count - MaxFields} more fields match; narrow the pattern");
        return sb.ToString().TrimEnd();
    }

    public static string Changes(string project, List<ChangeRecord> changes, DateTime sinceUtc, DateTime? refreshedUtc)
    {
        var since = sinceUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (changes.Count == 0)
            return $"no structure changes in {project} since {since}" +
                   (refreshedUtc is { } r ? $" (last refresh {Age(r)}; changes are recorded from the first refresh on)" : "");
        var sb = new StringBuilder().AppendLine($"{project} - {changes.Count} structure change(s) since {since}:");
        foreach (var c in changes)
            sb.AppendLine($"{c.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {c.Type ?? "(organization)"}: {c.Kind}{(c.Ref is null ? "" : $" {c.Ref}")}{(c.Detail.Length > 0 ? $" - {c.Detail}" : "")}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>"30d", "12h", "2w" or a date (yyyy-MM-dd) to UTC.</summary>
    public static DateTime ParseSince(string text, DateTime? now = null)
    {
        var t = text.Trim().ToLowerInvariant();
        var n = now ?? DateTime.UtcNow;
        if (t.Length > 1 && int.TryParse(t[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var k))
            switch (t[^1])
            {
                case 'h': return n.AddHours(-k);
                case 'd': return n.AddDays(-k);
                case 'w': return n.AddDays(-7 * k);
                case 'm': return n.AddMonths(-k);
            }
        if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return d.ToUniversalTime();
        throw new ArgumentException($"cannot read '{text}' as a time: use e.g. 30d, 12h, 2w, 3m or 2026-09-01");
    }

    public static System.Text.RegularExpressions.Regex Wildcard(string pattern)
    {
        var p = pattern.Trim();
        if (!p.Contains('*') && !p.Contains('?')) p = "*" + p + "*";
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return new System.Text.RegularExpressions.Regex(rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>One line, HTML tags and repeated whitespace removed, cut at <paramref name="max"/> characters.</summary>
    public static string Collapse(string text, int max)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        s = System.Text.RegularExpressions.Regex.Replace(System.Net.WebUtility.HtmlDecode(s), @"\s+", " ").Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
