using System.Reflection;
using ModelContextProtocol.Server;

namespace AdoLens;

/// <summary>Marks a tool class (or one tool method, which then overrides its class) with the group it belongs to.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ToolsetAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Tool groups and modes. Every tool definition is paid in tokens in every session, so a session starts with the small
/// core and Claude loads a group (ado_tools enable=…) when the conversation needs it. A mode only says which groups may
/// be loaded - never a permission; the PAT decides what is allowed.
/// </summary>
public static class Toolsets
{
    public const string Core = "core", Write = "write", Boards = "boards", Wiki = "wiki", Code = "code",
        Tests = "tests", Recipes = "recipes", Pr = "pr", Pipelines = "pipelines";

    public static readonly string[] All = [Core, Write, Boards, Wiki, Code, Tests, Recipes, Pr, Pipelines];

    /// <summary>What a group is for, in one line (the ado_tools description and answer).</summary>
    public static readonly Dictionary<string, string> About = new(StringComparer.OrdinalIgnoreCase)
    {
        [Write] = "create, change, comment, link, undo work items; fields and states of the process",
        [Boards] = "sprint, board, backlog, my day, backlog health, metrics, release notes",
        [Wiki] = "read, search and write wiki pages",
        [Code] = "changes of a changeset/PR/commit, files and their history, D365 labels, checks before testing, attachments",
        [Tests] = "test cases and their results",
        [Recipes] = "saved repeatable changes",
        [Pr] = "pull requests: lists, merged into a branch, details, create, comment, vote, tag, complete",
        [Pipelines] = "builds, why they failed, deployments, artifacts, running a pipeline",
    };

    public static readonly Dictionary<string, string[]> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["po"] = [Core, Write, Boards, Wiki, Code, Tests, Recipes],
        ["dev"] = [Core, Write, Wiki, Code, Pr, Pipelines, Recipes],
        ["full"] = All,
    };

    /// <summary>Old tool set names (before 0.8) still read from settings.</summary>
    static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["work"] = Write, ["trace"] = Code, ["test"] = Tests,
    };

    /// <summary>The groups the mode allows (plus extra ones from the settings).</summary>
    public static HashSet<string> Active(Settings settings)
    {
        var set = new HashSet<string>(Modes.GetValueOrDefault(settings.Mode) ?? Modes["po"], StringComparer.OrdinalIgnoreCase);
        foreach (var extra in settings.ExtraToolsets)
            if (TryNormalize(extra) is { } n) set.Add(n);
        set.Add(Core);
        return set;
    }

    /// <summary>
    /// The groups a session starts with: the core, or every allowed group with ToolLoading=all. With auto the rest is
    /// added before the first tool list for a client that does not take new tools (see <see cref="ToolGroups.ForClient"/>).
    /// </summary>
    public static HashSet<string> Loaded(Settings settings) =>
        settings.ToolLoading == "all" ? Active(settings) : new HashSet<string>([Core], StringComparer.OrdinalIgnoreCase);

    /// <summary>Every tool method with its group: the method's own attribute, else its class's.</summary>
    public static IEnumerable<(string Group, MethodInfo Method)> Catalog() =>
        typeof(Toolsets).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(m => ((m.GetCustomAttribute<ToolsetAttribute>() ?? t.GetCustomAttribute<ToolsetAttribute>())?.Name ?? Core, m)));

    public static IEnumerable<MethodInfo> Methods(IEnumerable<string> groups)
    {
        var set = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
        return Catalog().Where(c => set.Contains(c.Group)).Select(c => c.Method);
    }

    public static string ToolName(MethodInfo m) => m.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? m.Name;

    /// <summary>Registers the tools a session starts with.</summary>
    public static Microsoft.Extensions.DependencyInjection.IMcpServerBuilder WithActiveTools(
        this Microsoft.Extensions.DependencyInjection.IMcpServerBuilder builder, Settings settings)
    {
        foreach (var method in Methods(Loaded(settings)))
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(builder.Services,
                sp => ToolArguments.Tool(method, sp));
        return builder;
    }

    public static string? TryNormalize(string name)
    {
        var n = name.Trim();
        if (Renamed.TryGetValue(n, out var renamed)) return renamed;
        return All.FirstOrDefault(t => t.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string name) =>
        TryNormalize(name) ?? throw new ArgumentException($"unknown tool group '{name}' (known: {string.Join(", ", All)})");
}
