using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>
/// Project and code wikis: page tree, page content (already markdown), search, and writes guarded by the page version
/// (ETag), so a page changed by someone else since it was read is never overwritten.
/// </summary>
public static partial class WikiQueries
{
    const int MaxContent = 12000;
    public static readonly string[] Modes = ["create", "replace", "append", "prepend"];

    sealed record WikiRef(string Id, string Name, string Type, string? Branch);

    static async Task<WikiRef> PickAsync(AdoClient client, string project, string? wiki, CancellationToken ct)
    {
        var list = await client.GetListAsync($"{AdoClient.Scope(project)}_apis/wiki/wikis", ct);
        var wikis = list.Select(w => new WikiRef(Json.Str(w, "id") ?? "", Json.Str(w, "name") ?? "", Json.Str(w, "type") ?? "",
            Json.Arr(w, "versions").Select(v => Json.Str(v, "version")).FirstOrDefault())).ToList();
        if (wikis.Count == 0) throw new InvalidOperationException($"project {project} has no wiki");
        if (string.IsNullOrWhiteSpace(wiki))
            return wikis.FirstOrDefault(w => w.Type == "projectWiki") ?? wikis[0];
        return wikis.FirstOrDefault(w => w.Name.Equals(wiki.Trim(), StringComparison.OrdinalIgnoreCase) || w.Id.Equals(wiki.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? wikis.SingleOrDefault(w => w.Name.Contains(wiki.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"no wiki '{wiki}'; wikis: {string.Join(", ", wikis.Select(w => w.Name))}");
    }

    static string PagesUrl(string project, WikiRef w, string path, string extra = "") =>
        $"{AdoClient.Scope(project)}_apis/wiki/wikis/{Uri.EscapeDataString(w.Id)}/pages?path={Uri.EscapeDataString(NormalizePath(path))}{extra}" +
        // Code wikis are folders in a Git branch: their pages need the branch.
        (w.Type == "codeWiki" && w.Branch is not null ? $"&versionDescriptor.version={Uri.EscapeDataString(w.Branch)}&versionDescriptor.versionType=branch" : "");

    /// <summary>"Team/Setup" → "/Team/Setup"; spaces and case kept.</summary>
    internal static string NormalizePath(string path)
    {
        var p = path.Trim().Replace('\\', '/');
        if (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) p = p[..^3];
        return "/" + p.Trim('/');
    }

    public static async Task<string> ReadAsync(Lens lens, string? connection, string? project, string? wiki, string? path, string? search,
        int depth, string? detail, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var name = session.Project(project);
        var client = session.Client;
        if (!string.IsNullOrWhiteSpace(search)) return await SearchAsync(client, name, search, ct);

        var w = await PickAsync(client, name, wiki, ct);
        if (string.IsNullOrWhiteSpace(path) || NormalizePath(path) == "/")
        {
            var root = await client.GetAsync(PagesUrl(name, w, "/", "&recursionLevel=full"), ct);
            var sb = new StringBuilder($"wiki {w.Name} ({(w.Type == "codeWiki" ? $"code wiki, branch {w.Branch}" : "project wiki")}) - {Count(root) - 1} pages\n");
            Tree(sb, root, 0, Math.Clamp(depth, 1, 10));
            sb.Append("path=<page> reads a page; search=<text> searches all wikis of the project.");
            return sb.ToString();
        }

        (JsonElement page, string? etag) = await GetPageAsync(client, name, w, path, ct);
        var content = Json.Str(page, "content") ?? "";
        var full = detail == "full";
        var text = new StringBuilder();
        text.AppendLine($"{Json.Str(page, "path")} (wiki {w.Name}, version {Version(etag)})");
        if (Json.Str(page, "remoteUrl") is { } url) text.AppendLine(url);
        var subs = Json.Arr(page, "subPages").Select(p => Json.Str(p, "path")).Where(p => p is not null).ToList();
        if (subs.Count > 0) text.AppendLine($"subpages ({subs.Count}): {string.Join("; ", subs.Take(30))}{(subs.Count > 30 ? "; …" : "")}");
        text.AppendLine();
        if (content.Length == 0) text.Append("(empty page)");
        else if (full || content.Length <= MaxContent) text.Append(content.TrimEnd());
        else text.Append(content[..MaxContent].TrimEnd()).Append($"\n\n… {content.Length - MaxContent} more characters - CUT: read with detail=full before replacing this page");
        return text.ToString();
    }

    static async Task<(JsonElement Page, string? ETag)> GetPageAsync(AdoClient client, string project, WikiRef w, string path, CancellationToken ct)
    {
        try
        {
            return await client.SendWithHeadersAsync(HttpMethod.Get, PagesUrl(project, w, path, "&includeContent=true&recursionLevel=oneLevel"), null, null, null, ct);
        }
        catch (AdoApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException($"no page {NormalizePath(path)} in wiki {w.Name}; the page tree (no path) lists the pages");
        }
    }

    /// <summary>The version to pass back when replacing a page (the ETag without quotes).</summary>
    static string Version(string? etag) =>
        etag is null ? "?" : (etag.StartsWith("W/", StringComparison.Ordinal) ? etag[2..] : etag).Trim('"');

    static int Count(JsonElement page) => 1 + Json.Arr(page, "subPages").Sum(Count);

    static void Tree(StringBuilder sb, JsonElement page, int level, int depth)
    {
        foreach (var sub in Json.Arr(page, "subPages").OrderBy(p => p.TryGetProperty("order", out var o) && o.TryGetInt32(out var n) ? n : int.MaxValue))
        {
            var path = Json.Str(sub, "path") ?? "";
            var name = path[(path.LastIndexOf('/') + 1)..];
            var below = Count(sub) - 1;
            sb.Append(new string(' ', level * 2)).Append("- ").Append(name);
            if (below > 0 && level + 1 >= depth) sb.Append($" (+{below})");
            sb.AppendLine();
            if (level + 1 < depth) Tree(sb, sub, level + 1, depth);
        }
    }

    [GeneratedRegex(@"</?highlighthit>")]
    private static partial Regex Highlight();

    static async Task<string> SearchAsync(AdoClient client, string project, string text, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["searchText"] = text,
            ["$top"] = 20,
            ["$skip"] = 0,
            ["includeFacets"] = false,
            ["filters"] = new Dictionary<string, string[]> { ["Project"] = [project] },
        };
        var root = await client.PostAsync(client.ServiceUrl("almsearch") + $"{Uri.EscapeDataString(project)}/_apis/search/wikisearchresults", body, ct);
        var results = Json.Arr(root, "results").ToList();
        var total = root.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : results.Count;
        if (results.Count == 0) return $"no wiki pages match '{text}' in {project}";
        var sb = new StringBuilder($"wiki pages matching '{text}': {(total > results.Count ? $"{results.Count} of {total}" : results.Count.ToString())}\n");
        foreach (var r in results)
        {
            var path = (Json.Str(r, "path") ?? Json.Str(r, "fileName") ?? "").Replace(".md", "").Replace('-', ' ');
            var snippet = Json.Arr(r, "hits").SelectMany(h => Json.Arr(h, "highlights")).Select(h => h.GetString() ?? "").FirstOrDefault();
            sb.Append($"- {path} (wiki {Json.Str(Json.Obj(r, "wiki"), "name")})");
            if (snippet is not null) sb.Append(": ").Append(Formatting.Collapse(Highlight().Replace(snippet, ""), 160));
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Creates or changes a page. replace needs the version from the last read, so changes made by someone else in the
    /// meantime are never overwritten; append and prepend read and write in one step with the current version.
    /// </summary>
    public static async Task<string> WriteAsync(Lens lens, string? connection, string? project, string? wiki, string path, string content,
        string? mode, string? version, string? comment, CancellationToken ct = default)
    {
        var m = (mode ?? "create").Trim().ToLowerInvariant();
        if (!Modes.Contains(m)) throw new ArgumentException($"mode is one of: {string.Join(", ", Modes)}");
        if (string.IsNullOrWhiteSpace(path) || NormalizePath(path) == "/") throw new ArgumentException("give the page path, e.g. Team/Release notes");
        var session = lens.For(connection);
        var name = session.Project(project);
        var client = session.Client;
        var w = await PickAsync(client, name, wiki, ct);
        var extra = string.IsNullOrWhiteSpace(comment) ? "" : "&comment=" + Uri.EscapeDataString(comment.Trim());

        string? ifMatch = null;
        var body = content;
        switch (m)
        {
            case "replace":
                if (string.IsNullOrWhiteSpace(version))
                    throw new InvalidOperationException("replace needs the page version: read the page with ado_wiki path=… and pass its version");
                ifMatch = $"\"{version.Trim().Trim('"')}\"";
                break;
            case "append" or "prepend":
                var (page, etag) = await GetPageAsync(client, name, w, path, ct);
                var current = (Json.Str(page, "content") ?? "").TrimEnd();
                body = m == "append" ? current + (current.Length > 0 ? "\n\n" : "") + content.Trim() : content.Trim() + "\n\n" + current;
                ifMatch = etag;
                break;
        }

        try
        {
            var headers = ifMatch is null ? null : new Dictionary<string, string> { ["If-Match"] = ifMatch };
            var (saved, newTag) = await client.SendWithHeadersAsync(HttpMethod.Put, PagesUrl(name, w, path, extra), new { content = body }, "application/json", headers, ct);
            var verb = m switch { "create" => "created", "replace" => "replaced", _ => m + "ed to" };
            return $"{verb} {Json.Str(saved, "path") ?? NormalizePath(path)} (wiki {w.Name}, new version {Version(newTag)}, {body.Length} characters)" +
                   (Json.Str(saved, "remoteUrl") is { } url ? "\n" + url : "");
        }
        catch (AdoApiException ex) when (m == "create" && ex.Status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.BadRequest)
        {
            // Creating over an existing page is refused by Azure DevOps (no If-Match); say what to do instead.
            throw new InvalidOperationException($"{NormalizePath(path)} already exists: use mode=append, or read it and use mode=replace with its version ({ex.Message})");
        }
        catch (AdoApiException ex) when (ex.Status is HttpStatusCode.PreconditionFailed)
        {
            throw new InvalidOperationException($"{NormalizePath(path)} was changed since it was read{(version is null ? "" : $" (version {version})")}: read it again, merge, and replace with the new version");
        }
    }
}
