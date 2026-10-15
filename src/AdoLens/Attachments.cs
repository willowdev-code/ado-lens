using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace AdoLens;

/// <summary>Files attached to work items: the list, their text (Word, Excel, plain text), and adding one.</summary>
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

    static string Size(long? bytes) => bytes is not { } b ? "" : b < 1024 ? $" ({b} B)" : b < 1024 * 1024 ? $" ({b / 1024} KB)" : $" ({b / 1024.0 / 1024:0.#} MB)";

    /// <summary>Without <paramref name="file"/>: the attachments of the item; with it (name or number): its text.</summary>
    public static async Task<string> ReadAsync(Lens lens, string? connection, int id, string? file, string? detail, CancellationToken ct = default)
    {
        var session = lens.For(connection);
        var items = await session.Work.GetAsync([id], relations: true, ct: ct);
        if (items.Count == 0) return $"no work item #{id} visible with this PAT";
        var list = List(items[0]);
        var head = WorkItemFormat.Header(items[0]);
        if (list.Count == 0) return $"{head}: no attachments";
        if (string.IsNullOrWhiteSpace(file))
            return $"{head}: {list.Count} attachment(s)\n" + string.Join('\n', list.Select((a, i) => $"{i + 1}. {a.Name}{Size(a.Size)}{(a.Comment is { Length: > 0 } c ? $" - {c}" : "")}")) +
                   "\nfile=<name or number> reads one (Word, Excel, text).";

        var pick = int.TryParse(file, out var n) && n >= 1 && n <= list.Count ? list[n - 1]
            : list.FirstOrDefault(a => a.Name.Equals(file.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? list.FirstOrDefault(a => a.Name.Contains(file.Trim(), StringComparison.OrdinalIgnoreCase));
        if (pick is null) return $"no attachment '{file}' on #{id}; attachments: {string.Join(", ", list.Select(a => a.Name))}";
        var bytes = await session.Client.GetBytesAsync(pick.Url + (pick.Url.Contains('?') ? "&" : "?") + "download=true", MaxDownload, ct);
        var text = Extract(pick.Name, bytes);
        if (text is null) return $"{pick.Name}{Size(bytes.Length)}: this kind of file cannot be read as text (open it from the work item)";
        var full = detail == "full";
        return $"{pick.Name}{Size(bytes.Length)} from {head}\n\n" +
               (full || text.Length <= MaxText ? text : text[..MaxText] + $"\n\n… {text.Length - MaxText} more characters (detail=full)");
    }

    /// <summary>The text of a file: Word and Excel (Office Open XML), or anything that is plain text.</summary>
    internal static string? Extract(string name, byte[] bytes)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        try
        {
            switch (ext)
            {
                case ".docx" or ".docm":
                    return Word(bytes);
                case ".xlsx" or ".xlsm":
                    return Excel(bytes);
                case ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".log" or ".sql" or ".cs" or ".xpp" or ".yml" or ".yaml" or ".html" or ".htm":
                    return Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { return null; }
        // Unknown extension: text when it has no zero bytes in the start.
        return bytes.Take(4096).Contains((byte)0) ? null : Encoding.UTF8.GetString(bytes);
    }

    static XDocument? Part(ZipArchive zip, string path) =>
        zip.GetEntry(path) is { } e ? XDocument.Load(e.Open()) : null;

    /// <summary>Paragraphs as lines, headings as markdown, tables as "a | b" rows.</summary>
    static string Word(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var doc = Part(zip, "word/document.xml") ?? throw new InvalidDataException();
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder();
        var body = doc.Root?.Element(w + "body");
        if (body is null) return "";
        foreach (var block in body.Elements())
        {
            if (block.Name == w + "p") sb.AppendLine(Paragraph(block, w));
            else if (block.Name == w + "tbl")
            {
                foreach (var row in block.Elements(w + "tr"))
                    sb.AppendLine("| " + string.Join(" | ", row.Elements(w + "tc").Select(c => string.Join(" ", c.Elements(w + "p").Select(p => Paragraph(p, w))).Trim())) + " |");
                sb.AppendLine();
            }
        }
        // Collapse runs of empty lines.
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    static string Paragraph(XElement p, XNamespace w)
    {
        var text = string.Concat(p.Descendants().Select(e => e.Name == w + "t" ? e.Value : e.Name == w + "tab" ? "\t" : e.Name == w + "br" ? "\n" : ""));
        var style = p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "";
        var level = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out var l) ? l
            : style.StartsWith("Nagwek", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[6..], out var l2) ? l2 : 0;
        if (level > 0 && text.Length > 0) return new string('#', Math.Min(level, 6)) + " " + text;
        if (p.Element(w + "pPr")?.Element(w + "numPr") is not null && text.Length > 0) return "- " + text;
        return text;
    }

    /// <summary>Each sheet as rows of "a | b | c" (shared strings resolved).</summary>
    static string Excel(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = Part(zip, "xl/sharedStrings.xml")?.Root?.Elements(x + "si").Select(si => string.Concat(si.Descendants(x + "t").Select(t => t.Value))).ToList() ?? [];
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName.Length).ThenBy(e => e.FullName))
        {
            sb.AppendLine($"## {Path.GetFileNameWithoutExtension(entry.FullName)}");
            var sheet = XDocument.Load(entry.Open());
            foreach (var row in sheet.Descendants(x + "row").Take(500))
            {
                var cells = row.Elements(x + "c").Select(c =>
                {
                    var v = c.Element(x + "v")?.Value ?? string.Concat(c.Descendants(x + "t").Select(t => t.Value));
                    return (string?)c.Attribute("t") == "s" && int.TryParse(v, out var i) && i < shared.Count ? shared[i] : v;
                }).ToList();
                if (cells.Any(c => c.Length > 0)) sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            }
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

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
