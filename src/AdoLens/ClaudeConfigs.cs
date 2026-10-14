using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdoLens;

/// <summary>Reads and edits the MCP server entries in the Claude Desktop and Claude Code configuration files.</summary>
public static class ClaudeConfigs
{
    public const string DefaultServerName = "ado-lens";
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Claude Desktop config files: %APPDATA%\Claude and, for the Microsoft Store (MSIX) app, its private copy of AppData.</summary>
    public static List<string> DesktopConfigPaths()
    {
        var list = new List<string>();
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
        if (File.Exists(roaming) || Directory.Exists(Path.GetDirectoryName(roaming)))
            list.Add(roaming);

        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
            foreach (var pkg in Directory.EnumerateDirectories(packages, "Claude_*"))
            {
                var p = Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
                if (File.Exists(p)) list.Add(p);
            }
        return list;
    }

    public static string CodeConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");

    /// <summary>Default: Claude Desktop, plus Claude Code when it is installed (its config exists) or explicitly asked for.</summary>
    public static List<(string Path, bool IsDesktop)> Targets(bool desktopOnly, bool codeOnly)
    {
        var list = new List<(string, bool)>();
        if (!codeOnly)
            list.AddRange(DesktopConfigPaths().Select(p => (p, true)));
        if (!desktopOnly && (codeOnly || File.Exists(CodeConfigPath)))
            list.Add((CodeConfigPath, false));
        return list;
    }

    public static bool IsUpToDate(string path, string name, string exe) =>
        Load(path)["mcpServers"]?[name] is JsonObject entry && IsUpToDate(entry, exe);

    static bool IsUpToDate(JsonObject entry, string exe) =>
        SamePath(Text(entry["command"]), exe)
        && entry["args"] is JsonArray { Count: 1 } args && Text(args[0]) == "mcp"
        && !(entry["env"] is JsonObject env && env.Any(kv => IsSecretVar(kv.Key)));

    /// <summary>Points the entry to <paramref name="exe"/>; PAT variables are dropped (PATs live in Credential Manager).</summary>
    /// <returns>false when the entry was already up to date.</returns>
    public static bool Register(string path, string name, string exe)
    {
        var root = Load(path);
        if (root["mcpServers"] is not JsonObject servers)
            root["mcpServers"] = servers = new JsonObject();
        var old = servers[name] as JsonObject;
        if (old is not null && IsUpToDate(old, exe))
            return false;

        var entry = new JsonObject { ["command"] = exe, ["args"] = new JsonArray("mcp") };
        if (old?["env"] is JsonObject oldEnv)
        {
            var kept = new JsonObject();
            foreach (var (key, value) in oldEnv)
                if (!IsSecretVar(key)) kept[key] = value?.DeepClone();
            if (kept.Count > 0) entry["env"] = kept;
        }
        servers[name] = entry;
        Save(path, root);
        return true;
    }

    public enum UnregisterResult { Removed, NotRegistered, OtherInstallation }

    public static UnregisterResult Unregister(string path, string name, string exe, bool any)
    {
        if (!File.Exists(path)) return UnregisterResult.NotRegistered;
        var root = Load(path);
        if (root["mcpServers"] is not JsonObject servers || servers[name] is not JsonObject entry)
            return UnregisterResult.NotRegistered;
        if (!any && !SamePath(Text(entry["command"]), exe))
            return UnregisterResult.OtherInstallation;
        servers.Remove(name);
        Save(path, root);
        return UnregisterResult.Removed;
    }

    public static string? RegisteredCommand(string path, string name) =>
        Text(Load(path)["mcpServers"]?[name]?["command"]);

    /// <summary>Other MCP entries (e.g. older Azure DevOps servers) that keep a PAT in plain text in this file.</summary>
    public static List<string> EntriesWithPlainTextPat(string path)
    {
        var list = new List<string>();
        if (!File.Exists(path) || Load(path)["mcpServers"] is not JsonObject servers) return list;
        foreach (var (name, node) in servers)
            if (node?["env"] is JsonObject env && env.Any(kv => IsSecretVar(kv.Key) && !string.IsNullOrWhiteSpace(Text(kv.Value))))
                list.Add(name);
        return list;
    }

    /// <summary>Claude Desktop keeps its configuration in memory and writes it back, so entries must change while it is closed.</summary>
    public static bool IsClaudeDesktopRunning()
    {
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            try
            {
                var file = p.MainModule?.FileName ?? "";
                // The Claude Code CLI is also claude.exe; only the desktop app lives in these folders.
                if (file.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)
                    || file.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // process exited or is not accessible
            }
            finally
            {
                p.Dispose();
            }
        }
        return false;
    }

    static bool IsSecretVar(string key) =>
        key.Contains("PAT", StringComparison.OrdinalIgnoreCase) || key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase);

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static bool SamePath(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    static JsonObject Load(string path) =>
        File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject();

    // Written in place (no temp file + rename): the Store app's config can be a hard link that must stay intact.
    static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, root.ToJsonString(Indented));
    }
}
