using System.Collections.Concurrent;

namespace AdoLens;

/// <summary>A resolved connection with its HTTP client and metadata.</summary>
public sealed record Session(Config Config, AdoClient Client, MetadataService Meta)
{
    public Identities People { get; } = new(Client);

    WorkItems? _work;
    public WorkItems Work => _work ??= new(Client, Meta, People, Config);

    public Code Code { get; } = new(Client);

    public TeamSettings Team { get; } = new(Client);

    /// <summary>The project given, else the connection's default project.</summary>
    public string Project(string? project) =>
        !string.IsNullOrWhiteSpace(project) ? project.Trim()
        : Config.Project ?? throw new InvalidOperationException(
            $"no project given and connection '{Config.Connection}' has no default project: pass project=<name> or run 'adolens config --connection {Config.Connection} --project <name>'");
}

/// <summary>
/// Everything the server shares between tool calls: settings and one session (client + metadata) per connection,
/// created on first use so a server with several connections only talks to the ones Claude needs.
/// </summary>
public sealed class Lens(Settings settings, string version, string dataDir,
    Func<string, string?> env, Func<string, string?> credential, Func<Config, AdoClient>? clientFactory = null, string? settingsPath = null)
{
    readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public Settings Settings => settings;
    public string Version => version;

    /// <summary>Where settings changed through tools (ado_settings) are saved.</summary>
    public string SettingsPath => settingsPath ?? Path.Combine(dataDir, "adolens.json");

    public static Lens Default(string version) =>
        new(Settings.Load(Settings.DefaultPath), version, Settings.DataDir, Environment.GetEnvironmentVariable, CredentialStore.Read,
            settingsPath: Settings.DefaultPath);

    /// <summary>Saves the settings and drops the cached sessions, so the next call sees the change.</summary>
    public void SaveSettings()
    {
        settings.Save(SettingsPath);
        _sessions.Clear();
    }

    public string MetaPath(string connection) => Path.Combine(dataDir, "meta", connection + ".db");

    /// <exception cref="InvalidOperationException">The connection is not configured or has no PAT.</exception>
    public Session For(string? connection)
    {
        var cfg = Config.Resolve(settings, connection, env, credential);
        return _sessions.GetOrAdd(cfg.Connection, _ =>
        {
            var client = clientFactory?.Invoke(cfg) ?? new AdoClient(AdoClient.CreateHttpClient(cfg.Organization, cfg.Pat, version), cfg.Organization);
            return new Session(cfg, client, new MetadataService(client, new MetaStore(MetaPath(cfg.Connection))));
        });
    }
}
