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

        var others = lens.Settings.Connections.Keys.Where(k => !k.Equals(cfg.Connection, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0) sb.AppendLine("other connections: " + string.Join(", ", others));

        var projects = session.Meta.Store.Projects();
        sb.Append(projects.Count == 0
            ? "metadata: none yet (fetched on first use)"
            : "metadata: " + string.Join("; ", projects.Select(p => $"{p.Name} {Formatting.Age(p.RefreshedUtc)}")));
        return sb.ToString();
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
