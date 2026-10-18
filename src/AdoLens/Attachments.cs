using System.Text;
using System.Text.Json;

namespace AdoLens;

/// <summary>Files attached to work items: the list, their content for the model, saving them, adding one.</summary>
public static class Attachments
{
    const long MaxDownload = 20 * 1024 * 1024;
    const int MaxText = 12000;
    const string Rel = "AttachedFile";

    sealed record Attached(string Name, string Url, long? Size, string? Comment);

    static List<Attached> List(JsonElement wi) =>
        Json.Arr(wi, "relations").Where(r => Json.Str(r, "rel") == Rel).Select(r =>
        {
            var a = Json.Obj(r, "attributes");
            return new Attached(Json.Str(a, "name") ?? "(no name)", Json.Str(r, "url") ?? "",
                a.TryGetProperty("resourceSize", out var s) && s.TryGetInt64(out var n) ? n : null, Json.Str(a, "comment"));
        }).ToList();

    static readonly System.Text.RegularExpressions.Regex Img =
        new(@"<img[^>]*?\bsrc=""(?<src>[^""]+/_apis/wit/attachments/[^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Images pasted into the text fields and the comments (stored as attachments, but not listed with them).</summary>
    static async Task<List<Attached>> InlineImagesAsync(Session session, JsonElement wi, CancellationToken ct)
    {
        var found = new List<Attached>();
        void From(string? html, string where)
        {
            if (string.IsNullOrEmpty(html)) return;
            foreach (System.Text.RegularExpressions.Match m in Img.Matches(html))
            {
                var url = System.Net.WebUtility.HtmlDecode(m.Groups["src"].Value);
                if (found.Any(f => f.Url == url)) continue;
                var name = System.Text.RegularExpressions.Regex.Match(url, @"fileName=(?<n>[^&]+)") is { Success: true } n ? Uri.UnescapeDataString(n.Groups["n"].Value) : "image.png";
                found.Add(new Attached(name, url, null, $"pasted in {where}"));
            }
        }
        var fields = Json.Obj(wi, "fields");
        if (fields.ValueKind == JsonValueKind.Object)
            foreach (var f in fields.EnumerateObject().Where(f => f.Value.ValueKind == JsonValueKind.String && f.Value.GetString()!.Contains("<img", StringComparison.OrdinalIgnoreCase)))
                From(f.Value.GetString(), f.Name.Split('.')[^1]);
        if (WorkItems.Field(wi, "System.TeamProject") is { } project)
            try
            {
                foreach (var c in await session.Work.CommentsAsync(project, WorkItems.Id(wi), 100, ct))
                    From(Json.Str(c, "text"), $"a comment by {Json.Str(Json.Obj(c, "createdBy"), "displayName")} {Json.Str(c, "createdDate")?[..10]}");
            }
            catch (AdoApiException) { }
        return found;
    }

    static string Size(long? bytes) => bytes is not { } b ? "" : b < 1024 ? $" ({b} B)" : b < 1024 * 1024 ? $" ({b / 1024} KB)" : $" ({b / 1024.0 / 1024:0.#} MB)";

    /// <summary>
    /// Without <paramref name="file"/>: the attachments of the item and the images pasted into it. With it (name or
    /// number): its content for the model - text of a document (PDF, Word, Excel, PowerPoint, e-mail, ZIP list, text),
    /// or images (pictures, scanned PDF pages); with <paramref name="save"/> the file is streamed to disk instead and
    /// only its path comes back. Files too large or not readable get a link and the hint to save them.
    /// </summary>
    public static async Task<(string Text, List<(byte[] Bytes, string Mime)> Images)> ReadAsync(Lens lens, string? connection, int id, string? file,
        string? detail, string? pages = null, string? part = null, bool save = false, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var items = await session.Work.GetAsync([id], relations: true, ct: ct);
        if (items.Count == 0) return ($"no work item #{id} visible with this PAT", []);
        var list = List(items[0]).Concat(await InlineImagesAsync(session, items[0], ct)).ToList();
        var head = WorkItemFormat.Header(items[0]);
        if (list.Count == 0) return ($"{head}: no attachments and no pasted images", []);
        if (string.IsNullOrWhiteSpace(file))
            return ($"{head}: {list.Count} file(s)\n" + string.Join('\n', list.Select((a, i) => $"{i + 1}. {a.Name}{Size(a.Size)}{(a.Comment is { Length: > 0 } c ? $" - {c}" : "")}")) +
                    "\nfile=<name or number> reads one (documents as text, images and scans shown); save=true saves it to disk (any size).", []);

        var same = list.Select((a, i) => (a, i)).Where(x => x.a.Name.Equals(file.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (same.Count > 1)
            return ($"{same.Count} files named {same[0].a.Name} on #{id}: pick one by number - {string.Join(", ", same.Select(x => $"file={x.i + 1}{Size(x.a.Size)}"))}", []);
        var pick = int.TryParse(file, out var n) && n >= 1 && n <= list.Count ? list[n - 1]
            : list.FirstOrDefault(a => a.Name.Equals(file.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? list.FirstOrDefault(a => a.Name.Contains(file.Trim(), StringComparison.OrdinalIgnoreCase));
        if (pick is null) return ($"no file '{file}' on #{id}; files: {string.Join(", ", list.Select(a => a.Name))}", []);
        var download = pick.Url + (pick.Url.Contains('?') ? "&" : "?") + "download=true";
        if (save)
            return (await Downloads.SaveAsync(session.Client, download, Downloads.BaseDir(lens.Settings), id.ToString(System.Globalization.CultureInfo.InvariantCulture), pick.Name, ct), []);

        string Link(string why) => $"{pick.Name}{Size(pick.Size)}: {why}.\nsave=true saves it to disk (streamed, any size); in a browser signed in to Azure DevOps: {pick.Url}";
        if (pick.Size > MaxDownload) return (Link($"larger than {MaxDownload / 1024 / 1024} MB, too large to read here"), []);
        byte[] bytes;
        try { bytes = await session.Client.GetBytesAsync(download, MaxDownload, ct); }
        catch (AdoApiException ex) when (ex.Message.Contains("larger than", StringComparison.Ordinal)) { return (Link(ex.Message), []); }

        var content = Documents.Read(pick.Name, bytes, pages, part);
        if (content.Text is null && content.Images.Count == 0) return (Link(content.Note ?? "nothing readable as text"), []);
        var sb = new StringBuilder($"{pick.Name} ({Documents.Size(bytes.Length)}, {content.Kind}) from {head}");
        if (pick.Comment is { Length: > 0 } where) sb.Append($" ({where})");
        if (content.Note is not null) sb.Append($"\n{content.Note}");
        if (content.Images.Count > 0) sb.Append($"\n{content.Images.Count} image(s) attached");
        if (content.Text is not null) sb.Append("\n\n").Append(Documents.Fit(content.Text, MaxText, detail == "full"));
        return (sb.ToString(), content.Images);
    }

    /// <summary>An image recognized by its first bytes (pasted images may have no usable file name).</summary>
    internal static string? ImageMime(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' ? "image/gif"
        : b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? "image/webp"
        : null;

    /// <summary>The text of a file, or null when it has none (images, binaries).</summary>
    internal static string? Extract(string name, byte[] bytes) => Documents.Read(name, bytes).Text;

    /// <summary>Attaches a local file to a work item.</summary>
    public static async Task<string> AddAsync(Lens lens, string? connection, int id, string path, string? comment, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new ArgumentException($"no file {path}");
        var info = new FileInfo(path);
        if (info.Length > 60L * 1024 * 1024) throw new ArgumentException("the file is larger than 60 MB");
        var session = lens.For(connection);
        var items = await session.Work.GetAsync([id], relations: false, ["System.Id", "System.TeamProject", "System.WorkItemType", "System.State", "System.Title"], ct);
        if (items.Count == 0) return $"no work item #{id} visible with this PAT";
        var project = WorkItems.Field(items[0], "System.TeamProject")!;
        var url = await session.Client.UploadAttachmentAsync(project, info.Name, await File.ReadAllBytesAsync(path, ct), ct);
        await session.Work.PatchAsync(id, [new { op = "add", path = "/relations/-", value = new { rel = Rel, url, attributes = new { comment = comment ?? "" } } }], ct);
        return $"attached {info.Name}{Size(info.Length)} to {WorkItemFormat.Header(items[0])}";
    }
}
