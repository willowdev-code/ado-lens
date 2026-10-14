using System.Text;

namespace AdoLens;

/// <summary>The answers behind the MCP tools and the matching CLI commands (same text in both).</summary>
public static class Queries
{
    public static async Task<string> StatusAsync(Lens lens, string? connection, bool offline, CancellationToken ct = default)
    {
        var sb = new StringBuilder().AppendLine($"ADO Lens {lens.Version}, mode {lens.Settings.Mode}");
        Session session;
        try
        {
            session = lens.For(connection);
        }
        catch (InvalidOperationException ex)
        {
            return sb.Append("not configured: ").Append(ex.Message).ToString();
        }

        var cfg = session.Config;
        var line = $"connection {cfg.Connection}: https://dev.azure.com/{cfg.Organization}" +
                   (cfg.Project is null ? ", no default project" : $", project {cfg.Project}") +
                   (cfg.Team is null ? "" : $", team {cfg.Team}");
        sb.AppendLine(line);
        if (!offline)
        {
            try { sb.AppendLine($"signed in as {await session.Client.WhoAmIAsync(ct)}"); }
            catch (Exception ex) when (ex is AdoApiException or HttpRequestException or TaskCanceledException)
            {
                sb.AppendLine("connection FAILED: " + ex.Message);
            }
        }
        sb.AppendLine($"PAT from {cfg.PatSource}");
        if (cfg.Warning is { } w) sb.AppendLine("warning: " + w);
        if (Updater.PendingNotice() is { } update) sb.AppendLine("update: " + update);

        var others = lens.Settings.Connections.Keys.Where(k => !k.Equals(cfg.Connection, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0) sb.AppendLine("other connections: " + string.Join(", ", others));

        var projects = session.Meta.Store.Projects();
        sb.Append(projects.Count == 0
            ? "metadata: none yet (fetched on first use)"
            : "metadata: " + string.Join("; ", projects.Select(p => $"{p.Name} {Formatting.Age(p.RefreshedUtc)}")));
        return sb.ToString();
    }

    /// <summary>
    /// Shows the settings of a connection, or replaces its environment map ("dev=DEV (sbx01); staging=UAT; master=PROD",
    /// in flow order; empty clears it). Saved for good, so the question is asked only once.
    /// </summary>
    public static string SettingsAnswer(Lens lens, string? connection, string? environments)
    {
        var cfg = lens.For(connection).Config;
        if (!lens.Settings.Connections.TryGetValue(cfg.Connection, out var conn))
            throw new InvalidOperationException($"connection '{cfg.Connection}' comes from environment variables and cannot be changed here");
        if (environments is not null)
        {
            conn.Environments = ParseEnvironments(environments);
            lens.SaveSettings();
        }
        var sb = new StringBuilder(environments is null ? "" : "saved.\n");
        sb.AppendLine($"connection {cfg.Connection}: https://dev.azure.com/{conn.Organization}, project {conn.Project ?? "-"}, team {conn.Team ?? "-"}, mode {lens.Settings.Mode}");
        sb.Append(conn.Environments.Count == 0 ? "local environments: not set"
            : "environments (in flow order): " + string.Join(" -> ", conn.Environments.Select(e => $"{e.Value} [{e.Key}]")));
        return sb.ToString();
    }

    /// <summary>
    /// The settings and the environment flow of a project: saved locally or (scope=team) on the project's wiki page for
    /// everyone; without a map, the flow detected from releases and branches, so Claude can ask the user once.
    /// </summary>
    public static async Task<string> SettingsAsync(Lens lens, string? connection, string? project, string? environments, string? scope, CancellationToken ct = default)
    {
        var team = (scope ?? "local").Trim().ToLowerInvariant() switch
        {
            "local" => false,
            "team" => true,
            _ => throw new ArgumentException("scope is local or team"),
        };
        if (environments is not null && team)
        {
            var session = lens.For(connection);
            var name = session.Project(project);
            var map = ParseEnvironments(environments);
            var url = await session.Team.SaveAsync(name, map, ct);
            return $"saved for the whole team on the wiki page \"{TeamSettings.PagePath}\" of {name}: " +
                   string.Join(" -> ", map.Select(e => $"{e.Value} [{e.Key}]")) + $"\n{url}";
        }
        var text = SettingsAnswer(lens, connection, environments);
        if (environments is not null) return text;

        var s = lens.For(connection);
        var p = s.Project(project);
        var (effective, source) = await EnvironmentFlow.EffectiveAsync(s, p, ct);
        if (effective.Count > 0)
            return text + $"\nin use for {p} ({source}): " + string.Join(" -> ", effective.Select(e => $"{e.Value} [{e.Key}]"));
        var detected = await EnvironmentFlow.DetectAsync(s, p, ct);
        return text + (detected.Count == 0
            ? $"\nno release stages or long-lived branches found in {p}"
            : $"\ndetected in {p}: {string.Join(", ", detected.Select(d => d.Guess is null ? d.Key : $"{d.Key} (looks like {d.Guess})"))}. " +
              "Ask the user what each is called and in which order code moves, then save with environments=\"key=name; …\" " +
              "(scope=team for everyone, after the user agrees).");
    }

    static Dictionary<string, string> ParseEnvironments(string environments)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in environments.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1) throw new ArgumentException($"'{part}' is not key=label (e.g. staging=UAT (sbx03))");
            map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return map;
    }

    public static async Task<string> FieldsAsync(Lens lens, string? connection, string? project, string? type, string? field,
        string? changes, bool all, bool refresh, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);

        Snapshot snap;
        var refreshing = false;
        string? changeNote = null;
        if (refresh)
        {
            var (s, found) = await session.Meta.RefreshAsync(name, ct);
            snap = s;
            changeNote = found.Count == 0 ? "refreshed: no structure changes" : $"refreshed: {found.Count} structure change(s), changes=1d lists them";
        }
        else
            (snap, refreshing) = await session.Meta.GetAsync(name, ct);

        string answer;
        if (changes is not null)
        {
            var since = Formatting.ParseSince(changes);
            answer = Formatting.Changes(snap.Project.Name, session.Meta.Store.Changes(snap.Project.Name, since), since, snap.Project.RefreshedUtc);
        }
        else if (field is not null)
            answer = Formatting.Field(snap, field, refreshing);
        else if (type is not null)
            answer = snap.FindType(type) is { } t
                ? Formatting.Type(snap, t, all, refreshing)
                : $"no work item type '{type}' in {snap.Project.Name}; types: " +
                  string.Join(", ", snap.Types.Where(x => !x.Disabled && x.Level != Levels.Hidden).Select(x => x.Name).Order(StringComparer.OrdinalIgnoreCase));
        else
            answer = Formatting.Types(snap, refreshing);

        return changeNote is null ? answer : changeNote + "\n" + answer;
    }
}
