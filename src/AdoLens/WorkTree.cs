using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>
/// A whole tree of work items from one indented list (Epic → Features → Requirements → Tasks), previewed first and
/// created top-down with parent links; one undo removes it all.
/// </summary>
public static partial class WorkTree
{
    public sealed record Node(int Level, string Type, string Title, string? Description, string? AssignedTo, List<Node> Children);

    [GeneratedRegex(@"^(?<indent>\s*)[-*]\s+(?<type>[^:]+?)\s*:\s*(?<title>.+?)\s*$")]
    private static partial Regex Line();

    [GeneratedRegex(@"\s@(?<who>[^@|]+?)\s*$")]
    private static partial Regex Assignee();

    /// <summary>
    /// "- Type: Title | description @Person", nested by indentation (2 spaces or a tab per level). Lines that are not
    /// items are added to the description of the item above.
    /// </summary>
    public static List<Node> Parse(string text)
    {
        var roots = new List<Node>();
        var stack = new List<(int Indent, Node Node)>();
        Node? last = null;
        foreach (var raw in text.Replace("\r", "").Replace("\t", "  ").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            var m = Line().Match(raw);
            if (!m.Success)
            {
                if (last is null) throw new ArgumentException($"'{raw.Trim()}' is not an item: write '- Type: Title', e.g. '- Task: Add the field'");
                ReplaceLast(roots, stack, last, last with { Description = ((last.Description ?? "") + "\n" + raw.Trim()).Trim() }, out last);
                continue;
            }
            var indent = m.Groups["indent"].Value.Length;
            var title = m.Groups["title"].Value;
            string? who = null;
            if (Assignee().Match(title) is { Success: true } a) { who = a.Groups["who"].Value.Trim(); title = title[..a.Index].Trim(); }
            string? description = null;
            var bar = title.IndexOf(" | ", StringComparison.Ordinal);
            if (bar > 0) { description = title[(bar + 3)..].Trim(); title = title[..bar].Trim(); }
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            var node = new Node(stack.Count, m.Groups["type"].Value.Trim(), title, description, who, []);
            if (stack.Count == 0) roots.Add(node);
            else stack[^1].Node.Children.Add(node);
            stack.Add((indent, node));
            last = node;
        }
        if (roots.Count == 0) throw new ArgumentException("no items: write one per line, '- Type: Title', children indented under their parent");
        return roots;
    }

    // Records are immutable: swap the node in its parent's list (or the roots) and on the stack.
    static void ReplaceLast(List<Node> roots, List<(int Indent, Node Node)> stack, Node old, Node updated, out Node last)
    {
        last = updated;
        var parentList = stack.Count >= 2 ? stack[^2].Node.Children : roots;
        var i = parentList.IndexOf(old);
        if (i >= 0) parentList[i] = updated;
        for (var k = 0; k < stack.Count; k++)
            if (ReferenceEquals(stack[k].Node, old)) stack[k] = (stack[k].Indent, updated);
    }

    static IEnumerable<Node> All(IEnumerable<Node> nodes) => nodes.SelectMany(n => new[] { n }.Concat(All(n.Children)));

    public static async Task<string> CreateAsync(Lens lens, string? connection, string? project, int? parent, string items, string? iteration, string? area,
        string? confirm, CancellationToken ct = default)
    {
        var roots = Parse(items);
        var session = lens.For(connection);
        var name = session.Project(project);
        var s = (await session.Meta.GetAsync(name, ct)).Snapshot;
        // Types checked before anything is written.
        var unknown = All(roots).Select(n => n.Type).Distinct(StringComparer.OrdinalIgnoreCase).Where(t => s.FindType(t) is not { Disabled: false }).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"unknown type(s): {string.Join(", ", unknown)}; types: {string.Join(", ", s.Types.Where(t => !t.Disabled && t.Level != Levels.Hidden).Select(t => t.Name).Order())}");

        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{parent}|{iteration}|{area}|{items}")))[..8].ToLowerInvariant();
        var count = All(roots).Count();
        string Render(Node n, string indent) =>
            $"{indent}- {s.FindType(n.Type)!.Name}: {n.Title}{(n.AssignedTo is null ? "" : $" (@{n.AssignedTo})")}{(n.Description is null ? "" : " - " + Formatting.Collapse(n.Description, 60))}\n" +
            string.Concat(n.Children.Select(c => Render(c, indent + "  ")));
        if (!string.Equals(confirm?.Trim(), token, StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder($"PREVIEW - nothing written yet. {count} work item(s)");
            if (parent is { } p) sb.Append($" under #{p}");
            if (iteration is not null) sb.Append($", iteration {iteration}");
            sb.AppendLine(":");
            foreach (var r in roots) sb.Append(Render(r, ""));
            return sb.Append($"Show this to the user. To create them, call again with the same arguments and confirm={token}.").ToString();
        }

        var batch = Audit.NewBatch();
        var lines = new List<string>();
        async Task Create(Node n, int? parentId, string indent)
        {
            var (id, _) = await WorkQueries.CreateItemAsync(lens, connection, name, n.Type, new WriteRequest
            {
                Title = n.Title, Description = n.Description, AssignedTo = n.AssignedTo, Parent = parentId, Iteration = iteration, Area = area,
            }, batch, ct);
            lines.Add($"{indent}#{id} {s.FindType(n.Type)!.Name}: {n.Title}");
            foreach (var c in n.Children) await Create(c, id, indent + "  ");
        }
        try
        {
            foreach (var r in roots) await Create(r, parent, "");
        }
        catch (Exception ex) when (ex is AdoApiException or InvalidOperationException)
        {
            return $"created {lines.Count} of {count}, then it failed: {ex.Message}\n{string.Join('\n', lines)}\nado_undo removes what was created.";
        }
        return $"created {count} work item(s):\n{string.Join('\n', lines)}\nado_undo removes them all.";
    }
}
