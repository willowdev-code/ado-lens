using System.Text;
using System.Xml.Linq;

namespace AdoLens;

/// <summary>Line diff (Myers) printed as unified hunks with a few lines of context.</summary>
public static class Diff
{
    public enum Op { Same, Del, Add }

    public static List<(Op Op, string Line)> Lines(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // Common prefix and suffix first: most edits touch a few lines of a long file.
        var start = 0;
        while (start < a.Count && start < b.Count && a[start] == b[start]) start++;
        var endA = a.Count;
        var endB = b.Count;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }

        var result = new List<(Op, string)>();
        for (var i = 0; i < start; i++) result.Add((Op.Same, a[i]));
        result.AddRange(Myers(a.Skip(start).Take(endA - start).ToList(), b.Skip(start).Take(endB - start).ToList()));
        for (var i = endA; i < a.Count; i++) result.Add((Op.Same, a[i]));
        return result;
    }

    static List<(Op, string)> Myers(List<string> a, List<string> b)
    {
        int n = a.Count, m = b.Count, max = n + m;
        if (n == 0) return b.Select(l => (Op.Add, l)).ToList();
        if (m == 0) return a.Select(l => (Op.Del, l)).ToList();
        var v = new int[2 * max + 2];
        var trace = new List<int[]>();
        for (var d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]) ? v[k + 1 + max] : v[k - 1 + max] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[k + max] = x;
                if (x >= n && y >= m) return Backtrack(trace, a, b, max);
            }
        }
        return [];
    }

    static List<(Op, string)> Backtrack(List<int[]> trace, List<string> a, List<string> b, int max)
    {
        var result = new List<(Op, string)>();
        int x = a.Count, y = b.Count;
        for (var d = trace.Count - 1; d > 0; d--)
        {
            var v = trace[d];
            var k = x - y;
            var prevK = k == -d || (k != d && v[k - 1 + max] < v[k + 1 + max]) ? k + 1 : k - 1;
            var prevX = v[prevK + max];
            var prevY = prevX - prevK;
            while (x > prevX && y > prevY) { result.Add((Op.Same, a[x - 1])); x--; y--; }
            if (x == prevX) result.Add((Op.Add, b[y - 1])); else result.Add((Op.Del, a[x - 1]));
            x = prevX;
            y = prevY;
        }
        while (x > 0 && y > 0) { result.Add((Op.Same, a[x - 1])); x--; y--; }
        result.Reverse();
        return result;
    }

    /// <summary>Unified hunks ("@@ -a,b +c,d @@") with <paramref name="context"/> lines around each change; null when equal.</summary>
    public static string? Unified(string before, string after, int context = 3, int maxLines = 400)
    {
        var a = Split(before);
        var b = Split(after);
        var ops = Lines(a, b);
        if (ops.All(o => o.Op == Op.Same)) return null;

        // Line numbers in both versions at every position of the edit script.
        var lineA = new int[ops.Count];
        var lineB = new int[ops.Count];
        for (int i = 0, x = 0, y = 0; i < ops.Count; i++)
        {
            lineA[i] = x;
            lineB[i] = y;
            if (ops[i].Op != Op.Add) x++;
            if (ops[i].Op != Op.Del) y++;
        }

        // Changes closer than two contexts apart share a hunk.
        var changes = Enumerable.Range(0, ops.Count).Where(i => ops[i].Op != Op.Same).ToList();
        var hunks = new List<(int From, int To)>();
        int first = changes[0], last = changes[0];
        foreach (var c in changes.Skip(1))
        {
            if (c - last > 2 * context) { hunks.Add((first, last)); first = c; }
            last = c;
        }
        hunks.Add((first, last));

        var sb = new StringBuilder();
        var printed = 0;
        foreach (var (f, l) in hunks)
        {
            var from = Math.Max(0, f - context);
            var to = Math.Min(ops.Count - 1, l + context);
            var hunk = ops.Skip(from).Take(to - from + 1).ToList();
            sb.AppendLine($"@@ -{lineA[from] + 1},{hunk.Count(o => o.Op != Op.Add)} +{lineB[from] + 1},{hunk.Count(o => o.Op != Op.Del)} @@");
            foreach (var (op, line) in hunk)
            {
                if (printed++ >= maxLines) { sb.AppendLine($"… diff cut after {maxLines} lines"); return sb.ToString().TrimEnd(); }
                sb.Append(op switch { Op.Add => '+', Op.Del => '-', _ => ' ' }).AppendLine(line);
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static string[] Split(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
}

/// <summary>
/// Dynamics 365 F&amp;O metadata (AOT XML: AxClass, AxTable, AxForm…): changes per X++ method instead of raw XML,
/// plus a compact diff of the metadata around them (fields, controls, properties).
/// </summary>
public static class AotDiff
{
    public static bool IsAot(string path) =>
        path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
        && path.Replace('\\', '/').Split('/').Any(p => p.StartsWith("Ax", StringComparison.Ordinal) && p.Length > 2 && char.IsUpper(p[2]));

    /// <summary>"AxClass/SalesTable_Extension.xml" → "SalesTable_Extension (class)".</summary>
    public static string ObjectName(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        var name = Path.GetFileNameWithoutExtension(parts[^1]);
        var kind = parts.Length > 1 && parts[^2].StartsWith("Ax", StringComparison.Ordinal) ? parts[^2][2..] : "";
        kind = kind switch
        {
            "MenuItemDisplay" => "display menu item", "MenuItemAction" => "action menu item", "MenuItemOutput" => "output menu item",
            "DataEntityView" => "data entity", "SecurityPrivilege" => "privilege", "SecurityDuty" => "duty", "SecurityRole" => "role",
            _ when kind.StartsWith("Edt", StringComparison.Ordinal) => kind.EndsWith("Extension", StringComparison.Ordinal) ? "EDT extension" : "EDT",
            // TableExtension → "table extension", MenuExtension → "menu extension"
            _ => string.Concat(kind.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : char.ToLowerInvariant(ch).ToString())),
        };
        return kind.Length == 0 ? name : $"{name} ({kind})";
    }

    /// <summary>Methods by qualified name ("DataSource.method" in forms) with their source.</summary>
    public static Dictionary<string, string> Methods(XDocument? doc)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc?.Root is null) return map;
        if (doc.Root.Element("SourceCode")?.Element("Declaration")?.Value is { } decl) map["(declaration)"] = decl;
        foreach (var m in doc.Descendants().Where(e => e.Name.LocalName == "Method"))
        {
            var name = m.Element(m.Name.Namespace + "Name")?.Value ?? m.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
            var source = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Source")?.Value;
            if (name is null || source is null) continue;
            // The nearest named owner other than the object itself: a data source, control or field of a form or table.
            var owner = m.Ancestors().Skip(1).FirstOrDefault(a => a.Parent is not null && a.Parent != doc.Root && a.Elements().Any(e => e.Name.LocalName == "Name")
                                                                   && a.Name.LocalName is not ("Methods" or "SourceCode"));
            var ownerName = owner?.Elements().First(e => e.Name.LocalName == "Name").Value;
            var key = ownerName is null ? name : $"{ownerName}.{name}";
            map[key] = source;
        }
        return map;
    }

    /// <summary>The XML without method bodies: what is left are fields, controls and properties.</summary>
    /// <summary>Collections shown in a skeleton, with the properties worth a word for their items.</summary>
    static readonly Dictionary<string, string[]> SkeletonParts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fields"] = ["ExtendedDataType", "EnumType", "Label", "Mandatory", "StringSize"],
        ["FieldModifications"] = ["Label", "Mandatory", "ExtendedDataType"],
        ["FieldGroups"] = [],
        ["FieldGroupExtensions"] = [],
        ["Indexes"] = ["AllowDuplicates", "AlternateKey"],
        ["Relations"] = ["RelatedTable", "Cardinality", "RelationshipType"],
        ["RelationExtensions"] = [],
        ["DataSources"] = ["Table"],
        ["Mappings"] = [],
    };

    static readonly string[] SkeletonRoot = ["Label", "TableGroup", "TableType", "Extends", "ConfigurationKey", "PrimaryIndex", "ClusteredIndex", "TitleField1", "TitleField2"];

    /// <summary>
    /// The metadata of an AOT object in a few lines (table, table extension, view, map): properties, then each field with
    /// its type and label, indexes with their fields, relations with their table - instead of the raw XML.
    /// </summary>
    public static string Skeleton(XDocument? doc)
    {
        if (doc?.Root is not { } root) return "";
        var sb = new StringBuilder();
        string? Value(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        var props = SkeletonRoot.Select(p => (p, v: Value(root, p))).Where(x => x.v is not null).Select(x => $"{x.p} {x.v}").ToList();
        if (props.Count > 0) sb.AppendLine(string.Join("; ", props));
        foreach (var part in root.Elements().Where(e => SkeletonParts.ContainsKey(e.Name.LocalName)))
        {
            var items = part.Elements().ToList();
            if (items.Count == 0) continue;
            var keys = SkeletonParts[part.Name.LocalName];
            sb.AppendLine($"{part.Name.LocalName} ({items.Count}):");
            foreach (var item in items.Take(80))
            {
                var name = Value(item, "Name") ?? item.Name.LocalName;
                var extra = keys.Select(k => (k, v: Value(item, k))).Where(x => x.v is not null)
                    .Select(x => x.k is "ExtendedDataType" or "EnumType" or "RelatedTable" or "Table" or "Label" ? x.v! : $"{x.k} {x.v}").ToList();
                // Index and group members, relation constraints: their field names.
                var members = item.Descendants().Where(d => d.Name.LocalName is "DataField" or "Field" && d.Parent != item)
                    .Select(d => d.Value.Trim()).Where(v => v.Length > 0 && !v.Contains('\n')).Distinct().Take(8).ToList();
                if (members.Count > 0) extra.Add("(" + string.Join(", ", members) + ")");
                sb.AppendLine($"- {name}{(extra.Count > 0 ? ": " + string.Join(", ", extra) : "")}");
            }
            if (items.Count > 80) sb.AppendLine($"- … {items.Count - 80} more (detail=full)");
        }
        return sb.ToString().TrimEnd();
    }

    public static string Metadata(XDocument? doc)
    {
        if (doc?.Root is null) return "";
        var copy = new XDocument(doc);
        foreach (var e in copy.Descendants().Where(e => e.Name.LocalName is "Source" or "Declaration").ToList()) e.Value = "…";
        return copy.ToString();
    }

    static XDocument? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try { return XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>One line: "+method a, ~method b, -method c; metadata changed".</summary>
    public static string Summary(string? before, string? after)
    {
        var a = Parse(before);
        var b = Parse(after);
        if (a is null && b is not null)
        {
            var methods = Methods(b).Keys.Where(k => k != "(declaration)").Order(StringComparer.OrdinalIgnoreCase).ToList();
            return methods.Count == 0 ? "new" : $"new, {methods.Count} method(s): {string.Join(", ", methods.Take(12))}{(methods.Count > 12 ? ", …" : "")}";
        }
        if (b is null) return a is null ? "not readable as AOT XML" : "deleted";
        var (added, removed, changed, metadata) = Compare(a, b);
        var parts = new List<string>();
        parts.AddRange(added.Select(m => "+" + m));
        parts.AddRange(changed.Select(m => "~" + m));
        parts.AddRange(removed.Select(m => "-" + m));
        if (metadata) parts.Add("metadata changed");
        return parts.Count == 0 ? "no change in methods or metadata" : string.Join(", ", parts);
    }

    static (List<string> Added, List<string> Removed, List<string> Changed, bool Metadata) Compare(XDocument? a, XDocument? b)
    {
        var ma = Methods(a);
        var mb = Methods(b);
        var added = mb.Keys.Where(k => !ma.ContainsKey(k)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var removed = ma.Keys.Where(k => !mb.ContainsKey(k)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var changed = mb.Keys.Where(k => ma.TryGetValue(k, out var s) && Normalize(s) != Normalize(mb[k])).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return (added, removed, changed, Normalize(Metadata(a)) != Normalize(Metadata(b)));
    }

    static string Normalize(string s) => string.Join('\n', Diff.Split(s).Select(l => l.TrimEnd()));

    /// <summary>Changed methods as unified diffs, new methods in full, removed ones named, then the metadata diff.</summary>
    public static string Detail(string? before, string? after, int maxLines = 400)
    {
        var a = Parse(before);
        var b = Parse(after);
        if (a is null && b is null) return "(not readable as AOT XML)";
        var (added, removed, changed, metadata) = Compare(a, b);
        var ma = Methods(a);
        var mb = Methods(b);
        var sb = new StringBuilder();
        foreach (var m in changed)
            sb.AppendLine($"~ {m}").AppendLine(Diff.Unified(ma[m], mb[m], 3, maxLines)).AppendLine();
        foreach (var m in added)
            sb.AppendLine($"+ {m} (new)").AppendLine(string.Join('\n', Diff.Split(mb[m]).Select(l => "+" + l))).AppendLine();
        foreach (var m in removed) sb.AppendLine($"- {m} (removed)").AppendLine();
        if (metadata)
            sb.AppendLine("metadata (fields, controls, properties; method bodies left out):")
              .AppendLine(Diff.Unified(Metadata(a), Metadata(b), 2, maxLines / 2) ?? "");
        var text = sb.ToString().TrimEnd();
        return text.Length == 0 ? "no change in methods or metadata (whitespace or encoding only)" : text;
    }
}
