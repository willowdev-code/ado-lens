using System.Text;
using ModelContextProtocol.Server;

namespace AdoLens;

/// <summary>
/// Loading tool groups during a conversation: the tools are added to the server's tool collection, which sends the
/// client a tools/list_changed notification, and from then on Claude can call them. Each process serves one session,
/// so what is loaded stays loaded for that conversation only.
/// </summary>
public static class ToolGroups
{
    /// <summary>Clients known to take new tools during a conversation (MCP tools/list_changed), by their clientInfo name.</summary>
    static readonly string[] TakeNewTools = ["claude-code"];

    static int _clientChecked;

    public static bool TakesNewTools(string? clientName) =>
        clientName is { } n && TakeNewTools.Any(c => n.Contains(c, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// ToolLoading=auto: before the first tool list, a client that is not known to take new tools gets every group of
    /// the mode, so nothing is missing there; the others keep the core and load groups on demand.
    /// </summary>
    public static void ForClient(Settings settings, McpServer server)
    {
        if (Interlocked.Exchange(ref _clientChecked, 1) == 1) return;
        if (settings.ToolLoading != "auto" || TakesNewTools(server.ClientInfo?.Name)) return;
        if (server.ServerOptions.ToolCollection is not { } tools) return;
        using (tools.DeferChangedEvents())
            foreach (var method in Toolsets.Methods(Toolsets.Active(settings)))
                tools.TryAdd(McpServerTool.Create(method, options: new McpServerToolCreateOptions { Services = server.Services }));
    }

    public static string Load(Settings settings, McpServer server, string? enable)
    {
        var tools = server.ServerOptions.ToolCollection ?? throw new InvalidOperationException("this server has no tool collection");
        var allowed = Toolsets.Active(settings);
        var loaded = new HashSet<string>(Toolsets.Catalog().Where(c => tools.TryGetPrimitive(Toolsets.ToolName(c.Method), out _)).Select(c => c.Group),
            StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(enable))
        {
            var sb = new StringBuilder($"tool groups (mode {settings.Mode}):\n");
            foreach (var g in Toolsets.All.Where(g => g != Toolsets.Core))
            {
                var state = loaded.Contains(g) ? "loaded" : allowed.Contains(g) ? "available" : "not in this mode";
                sb.AppendLine($"- {g} ({state}): {Toolsets.About[g]}");
            }
            return sb.Append("enable=<group> loads one; enable=\"write;boards\" several.").ToString();
        }

        var result = new StringBuilder();
        // One change notification for all the tools of the request, not one per tool.
        using (tools.DeferChangedEvents())
            foreach (var name in enable.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (Toolsets.TryNormalize(name) is not { } group || group == Toolsets.Core)
                {
                    result.AppendLine($"no group '{name}'; groups: {string.Join(", ", Toolsets.All.Where(g => g != Toolsets.Core))}");
                    continue;
                }
                if (!allowed.Contains(group))
                {
                    result.AppendLine($"{group} is not part of the {settings.Mode} mode (change the mode in the ADO Lens settings to use it)");
                    continue;
                }
                var added = new List<string>();
                foreach (var method in Toolsets.Methods([group]))
                {
                    var tool = McpServerTool.Create(method, options: new McpServerToolCreateOptions { Services = server.Services });
                    if (tools.TryAdd(tool)) added.Add(Toolsets.ToolName(method));
                }
                result.AppendLine(added.Count == 0
                    ? $"{group}: already loaded"
                    : $"{group} loaded: {string.Join(", ", added)}");
                if (added.Count > 0 && McpTools.Hints(group) is { Length: > 0 } hints) result.AppendLine(hints);
            }
        return result.ToString().TrimEnd();
    }
}
