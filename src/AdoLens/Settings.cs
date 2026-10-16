using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>One Azure DevOps organization with its defaults. The PAT is never stored here.</summary>
public sealed class Connection
{
    public string Organization { get; set; } = "";
    public string? Project { get; set; }
    public string? Team { get; set; }

    /// <summary>
    /// What a branch means for people: "staging" → "UAT", "master" → "PROD" (TFVC: the last part of the branch path,
    /// e.g. "Main"). Azure DevOps does not know it; ado_trace uses it to say where a change has arrived.
    /// </summary>
    public Dictionary<string, string> Environments { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Languages every label must have (e.g. en-US, pl); others found in the label files are not reported as missing.
    /// Empty: every language of the label file is required.
    /// </summary>
    public List<string> LabelLanguages { get; set; } = [];
}

/// <summary>Non-secret settings (adolens.json in the data folder). PATs live in Windows Credential Manager.</summary>
public sealed partial class Settings
{
    public const string Modes = "po, dev, full";

    public string? DefaultConnection { get; set; }
    public Dictionary<string, Connection> Connections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Which tool sets Claude sees: po, dev or full. Never a permission: the PAT decides what is allowed.</summary>
    public string Mode { get; set; } = "po";

    /// <summary>Tool sets added to the mode (e.g. "pipelines" for a PO who follows builds).</summary>
    public List<string> ExtraToolsets { get; set; } = [];

    /// <summary>Updates of more work items than this are previewed first and written only with the preview's token.</summary>
    public int ConfirmBulkAbove { get; set; } = 3;

    public bool UsageLog { get; set; } = true;

    /// <summary>Ends every answer with its estimated size, e.g. "[~230 tokens]" (a few tokens each, so off by default).</summary>
    public bool ShowTokens { get; set; }

    /// <summary>Language of the settings console and installer texts: "pl", "en", or null for the language of Windows.</summary>
    public string? Language { get; set; }

    /// <summary>
    /// Start every session with all tool groups of the mode instead of the core only (for clients that do not take new
    /// tools during a conversation; costs more tokens per session).
    /// </summary>
    public bool LoadAllTools { get; set; }

    /// <summary>Where files are saved (save=true); null: Downloads\ADO Lens.</summary>
    public string? DownloadDir { get; set; }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // People read and edit this file: "UAT + SCM" and Polish names stay as typed, not +.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>%LOCALAPPDATA%\ado-lens (ADOLENS_DATA_DIR overrides it): settings, metadata, usage log.</summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("ADOLENS_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ado-lens");

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("ADOLENS_CONFIG") is { Length: > 0 } p ? p : Path.Combine(DataDir, "adolens.json");

    public static Settings Load(string path)
    {
        if (!File.Exists(path)) return new();
        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new();
        // The deserializer replaces the dictionaries, so the case-insensitive comparers have to be restored.
        s.Connections = new Dictionary<string, Connection>(s.Connections, StringComparer.OrdinalIgnoreCase);
        foreach (var c in s.Connections.Values)
        {
            c.Environments = new Dictionary<string, string>(c.Environments ?? [], StringComparer.OrdinalIgnoreCase);
            c.LabelLanguages ??= [];
        }
        return s;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static string NormalizeMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "po" or "dev" or "full" => mode.Trim().ToLowerInvariant(),
        _ => throw new ArgumentException($"unknown mode '{mode}' (known: {Modes})"),
    };

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,48}[A-Za-z0-9])?$")]
    private static partial Regex OrgName();

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex ConnectionNamePattern();

    /// <summary>Accepts "contoso", "https://dev.azure.com/contoso[/...]" or "https://contoso.visualstudio.com".</summary>
    public static string NormalizeOrganization(string input)
    {
        var s = input.Trim();
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            if (uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
                s = uri.AbsolutePath.Trim('/').Split('/')[0];
            else if (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
                s = uri.Host[..uri.Host.IndexOf('.')];
            else
                throw new ArgumentException($"not an Azure DevOps Services URL: {input}");
        }
        if (!OrgName().IsMatch(s))
            throw new ArgumentException($"not a valid Azure DevOps organization name: {input}");
        return s;
    }

    /// <summary>The project part of a pasted URL (https://dev.azure.com/org/Project/...), if any.</summary>
    public static string? ProjectFromUrl(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return parts.Length > i && !parts[i].StartsWith('_') ? Uri.UnescapeDataString(parts[i]) : null;
    }

    /// <summary>Connection names: lower case letters, digits and '-', e.g. "contoso" or "contoso-test".</summary>
    public static string NormalizeConnectionName(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        if (!ConnectionNamePattern().IsMatch(s))
            throw new ArgumentException($"not a valid connection name: {input} (use letters, digits and '-')");
        return s;
    }
}

/// <summary>Effective settings of one connection, with the PAT and where it came from.</summary>
public sealed record Config(string Connection, string Organization, string Pat, string? Project, string? Team, string PatSource)
{
    /// <summary>Branch → environment label of the connection (see <see cref="AdoLens.Connection.Environments"/>).</summary>
    public IReadOnlyDictionary<string, string> Environments { get; init; } = new Dictionary<string, string>();

    /// <summary>See <see cref="AdoLens.Connection.LabelLanguages"/>.</summary>
    public IReadOnlyList<string> LabelLanguages { get; init; } = [];

    /// <summary>Whether a missing label text in this language is worth reporting.</summary>
    public bool LabelLanguageRequired(string language) =>
        LabelLanguages.Count == 0 || LabelLanguages.Any(l => l.Equals(language, StringComparison.OrdinalIgnoreCase));

    public const string ConnectionVar = "ADOLENS_CONNECTION";
    public const string OrgVar = "AZURE_DEVOPS_ORG";
    public const string PatVar = "AZURE_DEVOPS_PAT";
    public const string ProjectVar = "AZURE_DEVOPS_PROJECT";

    /// <summary>Set when a PAT variable exists too but differs from the one in Credential Manager (which wins).</summary>
    public string? Warning { get; init; }

    /// <summary>ADOLENS_PAT_CONTOSO_TEST for the connection "contoso-test".</summary>
    public static string PatVarFor(string connection) => "ADOLENS_PAT_" + connection.ToUpperInvariant().Replace('-', '_');

    /// <summary>
    /// The connection given, else ADOLENS_CONNECTION, else the default one (or the only one). Without stored connections
    /// AZURE_DEVOPS_ORG / AZURE_DEVOPS_PROJECT describe one (CI). The PAT: Credential Manager first, then
    /// ADOLENS_PAT_&lt;CONNECTION&gt;, then AZURE_DEVOPS_PAT.
    /// </summary>
    public static Config Resolve(Settings settings, string? connection, Func<string, string?> env, Func<string, string?> credential)
    {
        var name = NullIfEmpty(connection) ?? NullIfEmpty(env(ConnectionVar)) ?? NullIfEmpty(settings.DefaultConnection)
            ?? (settings.Connections.Count == 1 ? settings.Connections.Keys.First() : null);

        Connection conn;
        if (name is not null && settings.Connections.TryGetValue(name, out var stored))
            conn = stored;
        else if (name is not null && settings.Connections.Count > 0)
            throw new InvalidOperationException(
                $"no connection named '{name}' (known: {string.Join(", ", settings.Connections.Keys)}); add it with 'adolens login --connection {name}'");
        else if (NullIfEmpty(env(OrgVar)) is { } envOrg)
        {
            // A pasted project link works too (the Claude Desktop extension asks for "organization or project link").
            conn = new Connection { Organization = Normalize(envOrg), Project = NullIfEmpty(env(ProjectVar)) ?? Settings.ProjectFromUrl(envOrg) };
            name ??= conn.Organization.ToLowerInvariant();
        }
        else if (settings.Connections.Count > 1)
            throw new InvalidOperationException(
                $"several connections ({string.Join(", ", settings.Connections.Keys)}) and no default: pass connection=<name> or run 'adolens config --default-connection <name>'");
        else
            throw new InvalidOperationException($"no Azure DevOps connection configured: run 'adolens login' (or set {OrgVar} and {PatVar})");

        var org = Normalize(conn.Organization);
        var project = NullIfEmpty(conn.Project) ?? NullIfEmpty(env(ProjectVar));
        var envVar = PatVarFor(name);
        var envPat = NullIfEmpty(env(envVar)) is { } p1 ? (Value: p1, Var: envVar)
            : NullIfEmpty(env(PatVar)) is { } p2 ? (Value: p2, Var: PatVar) : (Value: (string?)null, Var: (string?)null);

        if (NullIfEmpty(credential(name)) is { } storedPat)
            return new Config(name, org, storedPat, project, NullIfEmpty(conn.Team), $"Windows Credential Manager ({CredentialStore.TargetFor(name)})")
            {
                Warning = envPat.Value is { } v && v != storedPat
                    ? $"{envPat.Var} is set too but differs from the PAT in Credential Manager (which is used)"
                    : null,
                Environments = conn.Environments,
                LabelLanguages = conn.LabelLanguages ?? [],
            };
        if (envPat.Value is { } pat)
            return new Config(name, org, pat, project, NullIfEmpty(conn.Team), $"environment variable {envPat.Var}") { Environments = conn.Environments, LabelLanguages = conn.LabelLanguages ?? [] };

        throw new InvalidOperationException(
            $"no PAT for connection '{name}': run 'adolens login --connection {name}' (stores it in Windows Credential Manager) or set {envVar}");
    }

    static string Normalize(string org)
    {
        try { return Settings.NormalizeOrganization(org); }
        catch (ArgumentException ex) { throw new InvalidOperationException(ex.Message); }
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Records print all members; keep the token out of logs and exception messages.
    public override string ToString() =>
        $"Config {{ Connection = {Connection}, Organization = {Organization}, Project = {Project}, Team = {Team}, PatSource = {PatSource} }}";
}
