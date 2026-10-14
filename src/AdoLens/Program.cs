using System.Reflection;
using AdoLens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
var cli = new Cli(cl, version);
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
            return cli.Stats();
        case "api":
            return await cli.ApiAsync();
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
    Usage.Enabled &= lens.Settings.UsageLog;

    var builder = Host.CreateApplicationBuilder([]);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);

    builder.Services.AddSingleton(lens);
    builder.Services
        .AddMcpServer(o =>
        {
            o.ServerInfo = new() { Name = "ado-lens", Version = version };
            o.ServerInstructions = McpTools.Instructions;
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly();
    await builder.Build().RunAsync();
    return 0;
}

static string AppVersion()
{
    var v = typeof(Lens).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    var plus = v.IndexOf('+');
    return plus < 0 ? v : v[..plus];
}
