using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AdoLens;

/// <summary>What a file is, found from its first bytes (and the inside of a ZIP), not from its name.</summary>
public enum FileKind { Text, Image, Word, Excel, PowerPoint, Pdf, OutlookMsg, Email, Zip, OldOffice, Binary }

/// <summary>A file read for the model: its text, page images (scans), and what else can be asked for.</summary>
public sealed record DocumentContent(FileKind Kind, string? Text, List<(byte[] Bytes, string Mime)> Images, string? Note = null);

/// <summary>
/// Reads files from Azure DevOps (attachments, artifacts) for the model: documents as text, images and scanned PDF
/// pages as images. Binary files are never turned into text; they are saved instead.
/// </summary>
public static partial class Documents
{
    public const int MaxImageBytes = 4 * 1024 * 1024;
    public const int MaxPdfImagePages = 3;

    public static FileKind Detect(string name, byte[] b)
    {
        bool Starts(params byte[] sig) => b.Length >= sig.Length && sig.Select((x, i) => b[i] == x).All(x => x);
        if (Attachments.ImageMime(b) is not null) return FileKind.Image;
        if (Starts(0x25, 0x50, 0x44, 0x46)) return FileKind.Pdf; // %PDF
        if (Starts(0x50, 0x4B, 0x03, 0x04) || Starts(0x50, 0x4B, 0x05, 0x06))
        {
            try
            {
                using var zip = new ZipArchive(new MemoryStream(b));
                if (zip.GetEntry("word/document.xml") is not null) return FileKind.Word;
                if (zip.GetEntry("xl/workbook.xml") is not null || zip.Entries.Any(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)))
                    return FileKind.Excel;
                if (zip.GetEntry("ppt/presentation.xml") is not null || zip.Entries.Any(e => e.FullName.StartsWith("ppt/slides/", StringComparison.OrdinalIgnoreCase)))
                    return FileKind.PowerPoint;
                return FileKind.Zip;
            }
            catch (InvalidDataException) { return FileKind.Binary; }
        }
        if (Starts(0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1))
            return OutlookMsg.IsMessage(b) ? FileKind.OutlookMsg : FileKind.OldOffice;
        if (LooksLikeText(b))
            return name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) || IsEml(Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 4000)))
                ? FileKind.Email : FileKind.Text;
        return FileKind.Binary;
    }

    /// <summary>Text: UTF-16 with a BOM, or no zero bytes and mostly printable characters in the first 8 KB.</summary>
    internal static bool LooksLikeText(byte[] b)
    {
        if (b.Length >= 2 && (b[0] == 0xFF && b[1] == 0xFE || b[0] == 0xFE && b[1] == 0xFF)) return true;
        var n = Math.Min(b.Length, 8192);
        if (n == 0) return true;
        var control = 0;
        for (var i = 0; i < n; i++)
        {
            if (b[i] == 0) return false;
            if (b[i] < 0x09 || b[i] is > 0x0D and < 0x20) control++;
        }
        return control * 100 / n < 2;
    }

    static string DecodeText(byte[] b) =>
        b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE ? Encoding.Unicode.GetString(b, 2, b.Length - 2)
        : b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2)
        : Encoding.UTF8.GetString(b).TrimStart('﻿');

    /// <summary>
    /// Reads a file. <paramref name="pages"/> ("3", "2-5") limits a PDF; <paramref name="part"/> names a file inside a
    /// ZIP. Null text with no images: nothing readable (the caller offers saving or a link).
    /// </summary>
    public static DocumentContent Read(string name, byte[] bytes, string? pages = null, string? part = null)
    {
        var kind = Detect(name, bytes);
        try
        {
            return kind switch
            {
                FileKind.Text => new(kind, DecodeText(bytes), []),
                FileKind.Image => bytes.Length > MaxImageBytes
                    ? new(kind, null, [], $"the image is too large to show (over {MaxImageBytes / 1024 / 1024} MB)")
                    : new(kind, null, [(bytes, Attachments.ImageMime(bytes)!)]),
                FileKind.Word => new(kind, Word(bytes), []),
                FileKind.Excel => new(kind, Excel(bytes), []),
                FileKind.PowerPoint => new(kind, PowerPoint(bytes), []),
                FileKind.OutlookMsg => new(kind, OutlookMsg.Read(bytes), []),
                FileKind.Email => new(kind, Eml.Read(DecodeText(bytes)), []),
                FileKind.Pdf => Pdf(bytes, pages),
                FileKind.Zip => Zip(name, bytes, part, pages),
                FileKind.OldOffice => new(kind, null, [], "an old Office format (.doc / .xls / .ppt) - save it and open it, or ask for it as .docx / .xlsx"),
                _ => new(kind, null, [], "a binary file - nothing to read as text"),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or IOException or InvalidOperationException or ArgumentException
                                       or IndexOutOfRangeException or UglyToad.PdfPig.Core.PdfDocumentFormatException)
        {
            return new(kind, null, [], $"the {kind} file could not be read ({ex.Message})");
        }
    }

    /// <summary>"3" → (3,3); "2-5" → (2,5); null → (1, max).</summary>
    internal static (int From, int To) PageRange(string? pages, int count)
    {
        if (string.IsNullOrWhiteSpace(pages)) return (1, count);
        var m = Regex.Match(pages, @"^\s*(\d+)\s*(?:-\s*(\d+))?\s*$");
        if (!m.Success) throw new ArgumentException("pages is a number or a range, e.g. 3 or 2-5");
        var from = Math.Max(1, int.Parse(m.Groups[1].Value));
        var to = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : from;
        return (from, Math.Min(Math.Max(from, to), count));
    }

    static XDocument? Part(ZipArchive zip, string path) =>
        zip.GetEntry(path) is { } e ? XDocument.Load(e.Open()) : null;

    /// <summary>Paragraphs as lines, headings as markdown, tables as "a | b" rows.</summary>
    internal static string Word(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var doc = Part(zip, "word/document.xml") ?? throw new InvalidDataException("no word/document.xml");
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
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }

    static string Paragraph(XElement p, XNamespace w)
    {
        var text = string.Concat(p.Descendants().Select(e => e.Name == w + "t" ? e.Value : e.Name == w + "tab" ? "\t" : e.Name == w + "br" ? "\n" : ""));
        var style = p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "";
        // "Heading1" in English Word, "Nagwek1" in a Polish one.
        var digits = new string(style.Where(char.IsDigit).ToArray());
        var level = (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || style.StartsWith("Nagwek", StringComparison.OrdinalIgnoreCase))
                    && int.TryParse(digits, out var l) ? l : 0;
        if (level > 0 && text.Length > 0) return new string('#', Math.Min(level, 6)) + " " + text;
        if (p.Element(w + "pPr")?.Element(w + "numPr") is not null && text.Length > 0) return "- " + text;
        return text;
    }

    /// <summary>Each sheet (by its name) as rows of "a | b | c".</summary>
    internal static string Excel(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        XNamespace x = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var shared = Part(zip, "xl/sharedStrings.xml")?.Root?.Elements(x + "si").Select(si => string.Concat(si.Descendants(x + "t").Select(t => t.Value))).ToList() ?? [];
        // Sheet names: workbook.xml names each sheet's relationship, workbook.xml.rels gives its file.
        var rels = Part(zip, "xl/_rels/workbook.xml.rels")?.Root?.Elements().ToDictionary(e => (string?)e.Attribute("Id") ?? "", e => (string?)e.Attribute("Target") ?? "") ?? [];
        var sheets = Part(zip, "xl/workbook.xml")?.Descendants(x + "sheet")
            .Select(s => (Name: (string?)s.Attribute("name") ?? "", Path: rels.GetValueOrDefault((string?)s.Attribute(r + "id") ?? "")))
            .Where(s => s.Path is not null).Select(s => (s.Name, Path: "xl/" + s.Path!.TrimStart('/').Replace("xl/", ""))).ToList() ?? [];
        if (sheets.Count == 0)
            sheets = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
                .Select(e => (Path.GetFileNameWithoutExtension(e.FullName), e.FullName)).ToList();
        var sb = new StringBuilder();
        foreach (var (sheetName, path) in sheets)
        {
            if (zip.GetEntry(path) is not { } entry) continue;
            sb.AppendLine($"## {sheetName}");
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

    /// <summary>A saved e-mail starts with header lines, among them MIME-Version and From or Subject.</summary>
    internal static bool IsEml(string head)
    {
        var lines = head.Replace("\r", "").Split('\n').TakeWhile(l => l.Length > 0).ToList();
        if (lines.Count < 2 || !Regex.IsMatch(lines[0], @"^[A-Za-z][A-Za-z0-9-]*:")) return false;
        bool Has(string h) => lines.Any(l => l.StartsWith(h + ":", StringComparison.OrdinalIgnoreCase));
        return Has("MIME-Version") && (Has("From") || Has("Subject"));
    }

    [GeneratedRegex(@"slide(\d+)\.xml$")]
    private static partial Regex SlideNumber();

    /// <summary>Each slide's text, in slide order, with its notes.</summary>
    internal static string PowerPoint(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var slides = zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(e => (N: int.TryParse(SlideNumber().Match(e.FullName).Groups[1].Value, out var n) ? n : 0, Entry: e)).OrderBy(s => s.N).ToList();
        var sb = new StringBuilder();
        foreach (var (n, entry) in slides)
        {
            var doc = XDocument.Load(entry.Open());
            var paragraphs = doc.Descendants(a + "p").Select(p => string.Concat(p.Descendants(a + "t").Select(t => t.Value)).Trim()).Where(t => t.Length > 0).ToList();
            sb.AppendLine($"## Slide {n}");
            foreach (var p in paragraphs) sb.AppendLine(p);
            if (zip.GetEntry($"ppt/notesSlides/notesSlide{n}.xml") is { } notes)
            {
                var text = string.Join(" ", XDocument.Load(notes.Open()).Descendants(a + "t").Select(t => t.Value)).Trim();
                if (text.Length > 0 && !Regex.IsMatch(text, @"^\d+$")) sb.AppendLine($"(notes: {text})");
            }
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// PDF text page by page. Pages with (almost) no text are scans: their embedded page images are returned instead,
    /// a few at a time.
    /// </summary>
    static DocumentContent Pdf(byte[] bytes, string? pages)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(bytes);
        var count = doc.NumberOfPages;
        var (from, to) = PageRange(pages, count);
        var sb = new StringBuilder();
        var images = new List<(byte[], string)>();
        var scanned = new List<int>();
        for (var i = from; i <= to; i++)
        {
            var page = doc.GetPage(i);
            var text = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page).Trim();
            if (text.Length >= 20)
            {
                sb.AppendLine($"## Page {i}").AppendLine(text).AppendLine();
                continue;
            }
            scanned.Add(i);
            if (images.Count >= MaxPdfImagePages) continue;
            foreach (var img in page.GetImages())
            {
                byte[]? data = null;
                string? mime = null;
                if (img.TryGetPng(out var png)) { data = png; mime = "image/png"; }
                else if (Attachments.ImageMime(img.RawBytes.ToArray()) is { } m) { data = img.RawBytes.ToArray(); mime = m; }
                if (data is null || data.Length > MaxImageBytes) continue;
                images.Add((data, mime!));
                sb.AppendLine($"## Page {i}: scanned - shown as an image");
                break;
            }
        }
        var note = new StringBuilder($"PDF, {count} page(s)");
        if (from != 1 || to != count) note.Append($", pages {from}-{to} shown");
        else if (count > 3) note.Append("; pages=3 or pages=2-5 reads a part");
        if (scanned.Count > 0)
        {
            note.Append($"; no text on page(s) {string.Join(", ", scanned.Take(20))} (scanned)");
            var notShown = scanned.Count - images.Count;
            if (notShown > 0) note.Append($" - {notShown} not shown: ask for them with pages=<n>");
        }
        return new DocumentContent(FileKind.Pdf, sb.Length > 0 ? sb.ToString().Trim() : null, images, note.ToString());
    }

    /// <summary>The list of files in a ZIP; with <paramref name="part"/>, one file inside it read like any other.</summary>
    static DocumentContent Zip(string name, byte[] bytes, string? part, string? pages)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var files = zip.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
        if (string.IsNullOrWhiteSpace(part))
        {
            var sb = new StringBuilder($"{files.Count} file(s) in {name}:\n");
            foreach (var e in files.Take(200)) sb.AppendLine($"- {e.FullName} ({Size(e.Length)})");
            if (files.Count > 200) sb.AppendLine($"- … {files.Count - 200} more");
            return new(FileKind.Zip, sb.Append("part=<path inside> reads one of them.").ToString(), []);
        }
        var entry = files.FirstOrDefault(e => e.FullName.Equals(part.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? files.FirstOrDefault(e => e.FullName.EndsWith(part.Trim(), StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new(FileKind.Zip, null, [], $"no '{part}' in {name}");
        if (entry.Length > 50L * 1024 * 1024) return new(FileKind.Zip, null, [], $"{entry.FullName} is {Size(entry.Length)} - save the ZIP and open it");
        using var ms = new MemoryStream();
        using (var s = entry.Open()) s.CopyTo(ms);
        var inner = Read(entry.Name, ms.ToArray(), pages);
        return inner with { Note = $"{entry.FullName} from {name}" + (inner.Note is null ? "" : $"; {inner.Note}") };
    }

    public static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024} KB" : $"{bytes / 1024.0 / 1024:0.#} MB";

    /// <summary>
    /// A long text cut for the model: the headings (an outline) and the start; the rest on request. Short texts and
    /// detail=full pass whole.
    /// </summary>
    public static string Fit(string text, int max, bool full)
    {
        if (full || text.Length <= max) return text;
        // Real headings only: "## Page 3" / "## Slide 2" are markers this reader adds, not the document's outline.
        var headings = text.Split('\n').Where(l => l.StartsWith('#') && !Regex.IsMatch(l, @"^#+\s*(Page|Slide) \d+")).Take(40).ToList();
        var sb = new StringBuilder($"{text.Length:N0} characters (~{text.Length / 4:N0} tokens) - the start below; detail=full for all");
        if (headings.Count > 0) sb.Append($"\noutline: {string.Join(" | ", headings.Select(h => h.TrimStart('#', ' ')))}");
        return sb.Append("\n\n").Append(text[..max]).Append("\n…").ToString();
    }
}
