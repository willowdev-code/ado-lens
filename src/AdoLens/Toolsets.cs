namespace AdoLens;

/// <summary>Marks a tool class with the tool set it belongs to.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolsetAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Tool sets and modes. A mode is only a choice of which tools Claude sees (fewer tools: better choices and fewer
/// tokens per session) — never a permission; the PAT decides what is allowed.
/// </summary>
public static class Toolsets
{
    public const string Core = "core", Work = "work", Boards = "boards", Wiki = "wiki", Trace = "trace", Code = "code",
        Pr = "pr", Pipelines = "pipelines", Test = "test", Recipes = "recipes";

    public static readonly string[] All = [Core, Work, Boards, Wiki, Trace, Code, Pr, Pipelines, Test, Recipes];

    public static readonly Dictionary<string, string[]> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["po"] = [Core, Work, Boards, Wiki, Trace, Test, Recipes],
        ["dev"] = [Core, Work, Wiki, Trace, Code, Pr, Pipelines, Recipes],
        ["full"] = All,
    };

    public static HashSet<string> Active(Settings settings)
    {
        var set = new HashSet<string>(Modes.GetValueOrDefault(settings.Mode) ?? Modes["po"], StringComparer.OrdinalIgnoreCase);
        set.UnionWith(settings.ExtraToolsets);
        set.Add(Core);
        return set;
    }

    /// <summary>Tool classes of the active tool sets.</summary>
    public static List<Type> TypesFor(Settings settings)
    {
        var active = Active(settings);
        return typeof(Toolsets).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(ToolsetAttribute), false) is [ToolsetAttribute a] && active.Contains(a.Name))
            .ToList();
    }

    /// <summary>
    /// Registers the tools of the active tool sets. The cast matters: a List&lt;Type&gt; would bind to the generic
    /// WithTools&lt;T&gt;(target) overload and register the list's own (no) methods.
    /// </summary>
    public static Microsoft.Extensions.DependencyInjection.IMcpServerBuilder WithActiveTools(
        this Microsoft.Extensions.DependencyInjection.IMcpServerBuilder builder, Settings settings) =>
        Microsoft.Extensions.DependencyInjection.McpServerBuilderExtensions.WithTools(builder, (IEnumerable<Type>)TypesFor(settings));

    public static string Normalize(string name) =>
        All.FirstOrDefault(t => t.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"unknown tool set '{name}' (known: {string.Join(", ", All)})");
}
