using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AdoLens;

/// <summary>
/// Settings shared by a team, kept in Azure DevOps itself: a page "ADO Lens" in the project wiki. Everyone using ADO Lens
/// on the project reads it, so a Product Owner configures nothing; a local map (adolens config --env) overrides it.
/// The page is plain markdown people can read and edit:
/// <code>
/// | Branch or release stage | Environment |
/// |---|---|
/// | dev | DEV (sbx01) |
/// | staging | UAT (sbx03) |
/// | master | PROD |
/// </code>
/// </summary>
public sealed class TeamSettings(AdoClient client)
{
    public const string PagePath = "/ADO Lens";
    static readonly TimeSpan Fresh = TimeSpan.FromHours(1);

    readonly ConcurrentDictionary<string, (DateTime Read, Dictionary<string, string>? Map)> _cache = new(StringComparer.OrdinalIgnoreCase);

    static string PageUrl(string project, string wikiId, string extra = "") =>
        $"{AdoClient.Scope(project)}_apis/wiki/wikis/{Uri.EscapeDataString(wikiId)}/pages?path={Uri.EscapeDataString(PagePath)}{extra}";

    async Task<string?> ProjectWikiAsync(string project, CancellationToken ct)
    {
        var wikis = await client.GetListAsync($"{AdoClient.Scope(project)}_apis/wiki/wikis", ct);
        return wikis.Where(w => Json.Str(w, "type") == "projectWiki").Select(w => Json.Str(w, "id")).FirstOrDefault();
    }

    /// <summary>The team's environment map of a project in flow order, or null when the page or the wiki does not exist.</summary>
    public async Task<Dictionary<string, string>?> EnvironmentsAsync(string project, CancellationToken ct)
    {
        if (_cache.TryGetValue(project, out var hit) && DateTime.UtcNow - hit.Read < Fresh) return hit.Map;
        Dictionary<string, string>? map = null;
        try
        {
            if (await ProjectWikiAsync(project, ct) is { } wiki)
                map = Parse(Json.Str(await client.GetAsync(PageUrl(project, wiki, "&includeContent=true"), ct), "content"));
        }
        catch (AdoApiException) { } // no page, no wiki, or no permission: no team map
        _cache[project] = (DateTime.UtcNow, map);
        return map;
    }

    /// <summary>Rows of the first two-column table: key | environment. Header and separator rows are skipped.</summary>
    internal static Dictionary<string, string>? Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inTable = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('|')) { if (inTable && map.Count > 0) break; continue; }
            var cells = line.Trim('|').Split('|').Select(c => c.Trim().Trim('`')).ToArray();
            if (cells.Length < 2 || cells.All(c => c.Replace("-", "").Replace(":", "").Length == 0)) { inTable = true; continue; }
            if (!inTable) { inTable = true; continue; } // header row
            if (cells[0].Length > 0 && cells[1].Length > 0) map[cells[0]] = cells[1];
        }
        return map.Count > 0 ? map : null;
    }

    public static string Render(IReadOnlyDictionary<string, string> map)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ADO Lens");
        sb.AppendLine();
        sb.AppendLine("Where code goes, in order. ADO Lens (the Azure DevOps tool for Claude) reads this table to say where a change is.");
        sb.AppendLine("A key is a branch (`dev`; for TFVC the last part of the branch path, e.g. `Main`), a release definition,");
        sb.AppendLine("or one release stage as `Definition/Stage`. Edit freely; keep the two columns.");
        sb.AppendLine();
        sb.AppendLine("| Branch or release stage | Environment |");
        sb.AppendLine("|---|---|");
        foreach (var (key, label) in map) sb.AppendLine($"| {key} | {label} |");
        return sb.ToString();
    }

    /// <summary>Creates or replaces the page (with the version read just before, so a parallel edit is not lost).</summary>
    public async Task<string> SaveAsync(string project, IReadOnlyDictionary<string, string> map, CancellationToken ct)
    {
        var wiki = await ProjectWikiAsync(project, ct) ?? throw new InvalidOperationException($"project {project} has no project wiki to keep team settings in");
        string? etag = null;
        try { (_, etag) = await client.SendWithHeadersAsync(HttpMethod.Get, PageUrl(project, wiki), null, null, null, ct); }
        catch (AdoApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound) { }
        var headers = etag is null ? null : new Dictionary<string, string> { ["If-Match"] = etag };
        var (page, _) = await client.SendWithHeadersAsync(HttpMethod.Put, PageUrl(project, wiki, "&comment=" + Uri.EscapeDataString("Environment map for ADO Lens")),
            new { content = Render(map) }, "application/json", headers, ct);
        _cache[project] = (DateTime.UtcNow, new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase));
        return Json.Str(page, "remoteUrl") ?? PagePath;
    }
}
