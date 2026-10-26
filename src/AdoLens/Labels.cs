using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdoLens;

/// <summary>
/// Dynamics 365 F&amp;O labels: from the text a user sees (or a label id) to the label, its translations and where the
/// code uses it - the object and the X++ method, or the field or control whose label it is.
/// </summary>
public static partial class Labels
{
    const int MaxLabels = 5;
    const int MaxUses = 15;

    [GeneratedRegex(@"^@?(?:(?<file>[A-Za-z0-9_]+):)?(?<id>[A-Za-z_][A-Za-z0-9_]*)$")]
    private static partial Regex LabelId();

    [GeneratedRegex(@"%\d+")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    /// <summary>A label file: "…/AxLabelFile/LabelResources/pl/Contoso.pl.label.txt" → (Contoso, pl, the branch it is on).</summary>
    sealed record LabelFile(string Path, string? RepoId, string FileId, string Language, string Root);

    static LabelFile? AsLabelFile(string path, string? repoId)
    {
        var p = path.Replace('\\', '/');
        var i = p.IndexOf("/AxLabelFile/LabelResources/", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || !p.EndsWith(".label.txt", StringComparison.OrdinalIgnoreCase)) return null;
        var name = p[(p.LastIndexOf('/') + 1)..^".label.txt".Length]; // Contoso.pl
        var dot = name.LastIndexOf('.');
        if (dot <= 0) return null;
        // The branch: what comes before /Metadata/ (TFVC "$/P/Trunk/Main"), else the repository root.
        var meta = p.IndexOf("/Metadata/", StringComparison.OrdinalIgnoreCase);
        return new LabelFile(p, repoId, name[..dot], name[(dot + 1)..], meta > 0 ? p[..meta] : "/");
    }

    /// <summary>"Id=Text" lines of a label file (comments start with " ;").</summary>
    internal static Dictionary<string, string> Parse(string content)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            if (raw.Length == 0 || raw[0] == ' ' || raw[0] == ';') continue;
            var eq = raw.IndexOf('=');
            if (eq > 0) map[raw[..eq].Trim().TrimStart('\uFEFF')] = raw[(eq + 1)..];
        }
        return map;
    }

    /// <summary>
    /// Whether a label text is what the user saw: the user's text inside it, or the label with its %1 %2 filled in
    /// ("No on-hand for item %1" matches "No on-hand for item 12345").
    /// </summary>
    internal static bool Matches(string label, string seen)
    {
        var l = label.Trim();
        var s = seen.Trim();
        if (l.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
        if (!Placeholder().IsMatch(l)) return false;
        var pattern = "^" + string.Join(".*?", Placeholder().Split(l).Select(Regex.Escape)) + "$";
        return Regex.IsMatch(s, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>The words of a seen message worth searching for: no numbers or ids, the longest few.</summary>
    internal static string SearchWords(string seen) =>
        string.Join(' ', NonWord().Split(seen).Where(w => w.Length > 2 && !w.Any(char.IsDigit)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(w => w.Length).Take(5));

    public static async Task<string> FindAsync(Lens lens, string? connection, string? project, string text, string? branch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("give the text of the message (as seen on screen) or a label id like @Model:Id");
        var session = lens.For(connection);
        var name = session.CodeProject(project);
        var client = session.Client;
        var asId = LabelId().Match(text.Trim()) is { Success: true } m && (text.Contains(':') || text.TrimStart().StartsWith('@') || !text.Contains(' ')) ? m : null;
        var query = asId is not null ? asId.Groups["id"].Value : SearchWords(text);
        if (query.Length == 0) throw new ArgumentException("the text has no words to search for");

        // Only label files. A pasted message has its %1 %2 filled in with values that are not in the label (an item
        // name, a number): numbers are left out of the words, and with no hit the search is repeated with fewer words.
        var hits = new List<Knowledge.CodeHit>();
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        try
        {
            for (var n = words.Count; n >= 1 && hits.Count == 0; n = n > 3 ? 3 : n - 1)
                (hits, _) = await Knowledge.CodeHitsAsync(client, name, string.Join(' ', words.Take(n)) + " file:*.label.txt", 200, null, ct);
        }
        catch (AdoApiException ex) { return $"labels can only be found with Code Search ({ex.Message})"; }

        var repoIds = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        async Task<string?> RepoId(Knowledge.CodeHit h)
        {
            if (h.Tfvc) return null;
            if (!repoIds.TryGetValue(h.Repo, out var id)) repoIds[h.Repo] = id = (await session.Code.RepoAsync(h.Repo, name, ct)).Id;
            return id;
        }
        var files = new List<LabelFile>();
        foreach (var h in hits)
            if (AsLabelFile(h.Path, await RepoId(h)) is { } lf) files.Add(lf);
        if (files.Count == 0) return $"no label file in {name} contains '{text}' (only labels of your own models are in the repository; Microsoft labels like @SYS… are not)";

        // One branch: the one asked for, else Main / main / master, else the first.
        var roots = files.Select(f => f.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var root = (branch is null ? null : roots.FirstOrDefault(r => r.EndsWith("/" + branch.Trim('/'), StringComparison.OrdinalIgnoreCase)))
                   ?? roots.FirstOrDefault(r => r.EndsWith("/Main", StringComparison.OrdinalIgnoreCase))
                   ?? roots.FirstOrDefault(r => r.EndsWith("/main", StringComparison.Ordinal) || r.EndsWith("/master", StringComparison.OrdinalIgnoreCase))
                   ?? roots[0];
        var onRoot = files.Where(f => f.Root.Equals(root, StringComparison.OrdinalIgnoreCase)).ToList();

        // The labels: lines of the matching files whose id (or text) is what was asked.
        var found = new List<(LabelFile File, string Id)>();
        foreach (var f in onRoot)
        {
            var content = await ReadAsync(client, f, ct);
            if (content is null) continue;
            foreach (var (id, value) in Parse(content))
                if (asId is not null ? id.Equals(asId.Groups["id"].Value, StringComparison.OrdinalIgnoreCase) && (!asId.Groups["file"].Success || asId.Groups["file"].Value.Equals(f.FileId, StringComparison.OrdinalIgnoreCase))
                    : Matches(value, text))
                    if (!found.Any(x => x.File.FileId == f.FileId && x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) found.Add((f, id));
        }
        if (found.Count == 0) return $"no label on {root} has the text '{text}'" + (roots.Count > 1 ? $" (branches with label files: {string.Join(", ", roots)})" : "");

        var sb = new StringBuilder();
        if (found.Count > MaxLabels) sb.AppendLine($"{found.Count} labels match; the first {MaxLabels} - give more of the text to narrow it down");
        foreach (var (file, id) in found.Take(MaxLabels))
        {
            var label = $"@{file.FileId}:{id}";
            sb.AppendLine($"{label} ({root})");
            // Every language of the label file: the text, or that it is missing.
            var folder = file.Path[..file.Path.IndexOf("/LabelResources/", StringComparison.OrdinalIgnoreCase)] + "/LabelResources";
            var languageFiles = await LanguagesAsync(client, folder, file, ct);
            foreach (var lang in session.Config.LabelLanguages.Where(l => !languageFiles.Any(x => x.Language.Equals(l, StringComparison.OrdinalIgnoreCase))))
                sb.AppendLine($"  {lang}: NO LABEL FILE (required, but {file.FileId} has no {lang} file)");
            foreach (var lf in languageFiles)
            {
                var content = await ReadAsync(client, lf, ct);
                var value = content is null ? null : Parse(content).GetValueOrDefault(id);
                // A language the connection does not require is left out when the label has no text in it.
                if (value is null && !session.Config.LabelLanguageRequired(lf.Language)) continue;
                sb.AppendLine($"  {lf.Language}: {(value is null ? "MISSING" : Formatting.Collapse(value, 200))}");
            }
            sb.AppendLine("  used in:");
            sb.Append(await UsesAsync(session, name, root, label, ct));
        }
        return sb.ToString().TrimEnd();
    }

    static Task<string?> ReadAsync(AdoClient client, LabelFile f, CancellationToken ct) =>
        Knowledge.Content(client, f.RepoId is null
            ? $"_apis/tfvc/items?path={Uri.EscapeDataString(f.Path)}&includeContent=true"
            : $"_apis/git/repositories/{f.RepoId}/items?path={Uri.EscapeDataString(f.Path)}&includeContent=true", ct);

    /// <summary>The label files of the same label file id in every language folder.</summary>
    static async Task<List<LabelFile>> LanguagesAsync(AdoClient client, string folder, LabelFile file, CancellationToken ct)
    {
        List<JsonElement> items;
        try
        {
            items = await client.GetListAsync(file.RepoId is null
                ? $"_apis/tfvc/items?scopePath={Uri.EscapeDataString(folder)}&recursionLevel=Full"
                : $"_apis/git/repositories/{file.RepoId}/items?scopePath={Uri.EscapeDataString(folder)}&recursionLevel=Full", ct);
        }
        catch (AdoApiException) { return [file]; }
        var list = items.Select(i => Json.Str(i, "path")).Where(p => p is not null).Select(p => AsLabelFile(p!, file.RepoId))
            .Where(l => l is not null && l.FileId.Equals(file.FileId, StringComparison.OrdinalIgnoreCase)).Select(l => l!)
            .OrderBy(l => l.Language, StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? [file] : list;
    }

    /// <summary>Objects using the label on the branch: X++ methods that show it, fields and controls it names.</summary>
    static async Task<string> UsesAsync(Session session, string project, string root, string label, CancellationToken ct)
    {
        List<Knowledge.CodeHit> hits;
        try { (hits, _) = await Knowledge.CodeHitsAsync(session.Client, project, label.TrimStart('@').Replace(':', ' '), 100, null, ct); }
        catch (AdoApiException ex) { return $"  ({ex.Message})\n"; }
        var uses = hits.Where(h => !h.Path.Contains("/AxLabelFile/", StringComparison.OrdinalIgnoreCase)
                                   && (root == "/" || h.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))).Take(MaxUses).ToList();
        if (uses.Count == 0) return "  - nowhere on this branch (unused, or used only through code that builds the id)\n";
        var lines = await Task.WhenAll(uses.Select(async h =>
        {
            var where = h.Tfvc ? h.Path : $"{h.Repo}: {h.Path}";
            if (!AotDiff.IsAot(h.Path)) return $"  - {where}";
            var repoId = h.Tfvc ? null : (await session.Code.RepoAsync(h.Repo, project, ct)).Id;
            var xml = await Knowledge.Content(session.Client, repoId is null
                ? $"_apis/tfvc/items?path={Uri.EscapeDataString(h.Path)}&includeContent=true"
                : $"_apis/git/repositories/{repoId}/items?path={Uri.EscapeDataString(h.Path)}&includeContent=true", ct);
            return $"  - {AotDiff.ObjectName(h.Path)}{Places(xml, label)}";
        }));
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>": method a, method b; label of field X" - where in an AOT object the label appears.</summary>
    internal static string Places(string? xml, string label)
    {
        if (xml is null) return "";
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return ""; }
        var methods = AotDiff.Methods(doc).Where(m => m.Value.Contains(label, StringComparison.OrdinalIgnoreCase)).Select(m => m.Key).ToList();
        // Metadata: an element whose value is the label (Label, HelpText, Caption…) inside a named field or control.
        var props = doc.Descendants().Where(e => !e.HasElements && e.Value.Trim().Equals(label, StringComparison.OrdinalIgnoreCase)
                                                 && e.Name.LocalName is not ("Source" or "Declaration"))
            .Select(e =>
            {
                var owner = e.Ancestors().FirstOrDefault(a => a.Parent is not null && a.Elements().Any(x => x.Name.LocalName == "Name"));
                var ownerName = owner?.Elements().First(x => x.Name.LocalName == "Name").Value;
                return ownerName is null ? e.Name.LocalName : $"{e.Name.LocalName} of {ownerName}";
            }).Distinct().ToList();
        var parts = new List<string>();
        if (methods.Count > 0) parts.Add(string.Join(", ", methods.Take(5)));
        if (props.Count > 0) parts.Add(string.Join(", ", props.Take(5)));
        return parts.Count == 0 ? "" : ": " + string.Join("; ", parts);
    }
}
