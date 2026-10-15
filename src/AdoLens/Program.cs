using System.Reflection;
using AdoLens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// "--tokens" works with every command: the answer is followed by its estimated size.
var showTokens = args.Skip(1).Contains("--tokens", StringComparer.OrdinalIgnoreCase);
args = [.. args.Take(1), .. args.Skip(1).Where(a => !a.Equals("--tokens", StringComparison.OrdinalIgnoreCase))];

CommandLine cl;
try
{
    cl = CommandLine.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var version = AppVersion();
var cli = new Cli(cl, version, showTokens);
try
{
    switch (cl.Command)
    {
        case "mcp":
            return await RunServerAsync();
        case "--version" or "-v" or "version":
            Console.WriteLine($"ado-lens {version}");
            return 0;
        case "login":
            return await cli.LoginAsync();
        case "logout":
            return cli.Logout();
        case "connections":
            return cli.Connections();
        case "config":
            return cli.Config();
        case "status":
            return await cli.StatusAsync();
        case "register":
            return cli.Register();
        case "unregister":
            return cli.Unregister();
        case "refresh":
            return await cli.RefreshAsync();
        case "fields":
            return await cli.FieldsAsync();
        case "stats":
            return await cli.StatsAsync();
        case "api":
            return await cli.ApiAsync();
        case "item":
            return await cli.ItemAsync();
        case "query":
            return await cli.QueryAsync();
        case "create":
            return await cli.CreateAsync();
        case "update":
            return await cli.UpdateAsync();
        case "comment":
            return await cli.CommentAsync();
        case "link":
            return await cli.LinkAsync();
        case "sprint":
            return await cli.SprintAsync();
        case "board":
            return await cli.BoardAsync();
        case "backlog":
            return await cli.BacklogAsync();
        case "myday":
            return await cli.MyDayAsync();
        case "upgrade":
            return await cli.UpdateCommandAsync();
        case "setup":
            return await cli.SetupAsync();
        case "trace":
            return await cli.TraceAsync();
        case "changes":
            return await cli.ChangesAsync();
        case "pr":
            return await cli.PrAsync();
        case "pipeline":
            return await cli.PipelineAsync();
        case "wiki":
            return await cli.WikiAsync();
        case "wiki-write":
            return await cli.WikiWriteAsync();
        case "search":
            return await cli.SearchAsync();
        case "label":
            return await cli.LabelAsync();
        case "file":
            return await cli.FileAsync();
        case "tests":
            return await cli.TestsAsync();
        case "test-create":
            return await cli.TestCreateAsync();
        case "health":
            return await cli.HealthAsync();
        case "release-notes":
            return await cli.ReleaseNotesAsync();
        case "undo":
            return await cli.UndoAsync();
        case "recipe":
            return await cli.RecipeAsync();
        case "help" or "--help" or "-h" or "/?":
            Cli.PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"unknown command: {args[0]}");
            Cli.PrintUsage();
            return 2;
    }
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or AdoApiException or HttpRequestException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

async Task<int> RunServerAsync()
{
    var lens = Lens.Default(version);
    // The Claude Desktop extension (.mcpb) chooses the mode in its settings.
    if (Environment.GetEnvironmentVariable("ADOLENS_MODE") is { Length: > 0 } mode)
    {
        try { lens.Settings.Mode = Settings.NormalizeMode(mode); }
        catch (ArgumentException) { }
    }
    Usage.Enabled &= lens.Settings.UsageLog;
    Updater.RemoveLeftovers();
    Updater.CheckInBackground();
    McpTools.ShowTokens = lens.Settings.ShowTokens;

    var builder = Host.CreateApplicationBuilder([]);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    var toolTypes = Toolsets.TypesFor(lens.Settings);
    if (Environment.GetEnvironmentVariable("ADOLENS_VERBOSE") == "1")
        Console.Error.WriteLine($"ado-lens: mode {lens.Settings.Mode}, tool classes: {string.Join(", ", toolTypes.Select(t => t.Name))}");

    builder.Services.AddSingleton(lens);
    builder.Services
        .AddMcpServer(o =>
        {
            o.ServerInfo = new() { Name = "ado-lens", Version = version };
            o.ServerInstructions = McpTools.Instructions(lens.Settings);
        })
        .WithStdioServerTransport()
        .WithActiveTools(lens.Settings);
    await builder.Build().RunAsync();
    return 0;
}

static string AppVersion()
{
    var v = typeof(Lens).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    var plus = v.IndexOf('+');
    return plus < 0 ? v : v[..plus];
}
