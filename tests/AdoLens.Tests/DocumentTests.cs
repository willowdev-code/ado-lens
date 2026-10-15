using System.IO.Compression;
using System.Net;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AdoLens.Tests;

public class DocumentTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"adolens-docs-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    static byte[] Zip(params (string Path, string Content)[] parts)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in parts)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        return ms.ToArray();
    }

    /// <summary>Page 1 with text, page 2 a "scan" (only an image), page 3 text again.</summary>
    static byte[] Pdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var p1 = builder.AddPage(PageSize.A4);
        p1.AddText("Credit limit check: orders above the limit are blocked.", 12, new PdfPoint(40, 700), font);
        var p2 = builder.AddPage(PageSize.A4);
        p2.AddPng(Png, new PdfRectangle(40, 400, 400, 760));
        var p3 = builder.AddPage(PageSize.A4);
        p3.AddText("The third page explains the settings of the check.", 12, new PdfPoint(40, 700), font);
        return builder.Build();
    }

    [Fact]
    public void Kinds_come_from_the_bytes_not_the_name()
    {
        Assert.Equal(FileKind.Pdf, Documents.Detect("spec.bin", Pdf()));
        Assert.Equal(FileKind.Word, Documents.Detect("x.zip", Zip(("word/document.xml", "<w:document/>"))));
        Assert.Equal(FileKind.Excel, Documents.Detect("x", Zip(("xl/workbook.xml", "<workbook/>"))));
        Assert.Equal(FileKind.PowerPoint, Documents.Detect("x", Zip(("ppt/presentation.xml", "<p/>"))));
        Assert.Equal(FileKind.Zip, Documents.Detect("logs.docx", Zip(("a.log", "x"))));
        Assert.Equal(FileKind.Image, Documents.Detect("shot.txt", Png));
        Assert.Equal(FileKind.Email, Documents.Detect("mail.txt", Encoding.UTF8.GetBytes("From: a@b.c\nSubject: x\nMIME-Version: 1.0\n\nhi")));
        Assert.Equal(FileKind.Text, Documents.Detect("notes", Encoding.UTF8.GetBytes("plain notes\nsecond line")));
        // A trace or any binary: never read as text, whatever its name.
        Assert.Equal(FileKind.Binary, Documents.Detect("trace.txt", [0x45, 0x6C, 0x66, 0x46, 0x69, 0x6C, 0x65, 0, 3, 0, 1, 2, 0, 0]));
        Assert.Null(Documents.Read("trace.etl", [1, 2, 3, 0, 5]).Text);
    }

    [Fact]
    public void Pdf_gives_text_per_page_and_scanned_pages_as_images()
    {
        var all = Documents.Read("spec.pdf", Pdf());
        Assert.Contains("## Page 1", all.Text);
        Assert.Contains("orders above the limit are blocked", all.Text);
        Assert.Contains("## Page 2: scanned - shown as an image", all.Text);
        Assert.Contains("third page", all.Text);
        Assert.Single(all.Images);
        Assert.Contains("PDF, 3 page(s)", all.Note);
        Assert.Contains("no text on page(s) 2 (scanned)", all.Note);

        var third = Documents.Read("spec.pdf", Pdf(), pages: "3");
        Assert.DoesNotContain("Page 1", third.Text);
        Assert.Contains("third page", third.Text);
        Assert.Empty(third.Images);
        Assert.Throws<ArgumentException>(() => Documents.PageRange("a-b", 3));
        Assert.Equal((2, 3), Documents.PageRange("2-9", 3));
    }

    [Fact]
    public void PowerPoint_slides_come_in_order_with_their_notes()
    {
        const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var pptx = Zip(
            ("ppt/presentation.xml", "<p/>"),
            ("ppt/slides/slide10.xml", $"""<sld xmlns:a="{a}"><a:p><a:r><a:t>Last slide</a:t></a:r></a:p></sld>"""),
            ("ppt/slides/slide2.xml", $"""<sld xmlns:a="{a}"><a:p><a:r><a:t>Second </a:t></a:r><a:r><a:t>slide</a:t></a:r></a:p></sld>"""),
            ("ppt/notesSlides/notesSlide2.xml", $"""<notes xmlns:a="{a}"><a:p><a:r><a:t>Say it slowly</a:t></a:r></a:p></notes>"""));
        var text = Documents.Read("deck.pptx", pptx).Text!.Replace("\r", "");
        Assert.True(text.IndexOf("## Slide 2", StringComparison.Ordinal) < text.IndexOf("## Slide 10", StringComparison.Ordinal));
        Assert.Contains("## Slide 2\nSecond slide\n(notes: Say it slowly)", text);
    }

    [Fact]
    public void Saved_emails_are_decoded()
    {
        const string eml = """
            From: =?UTF-8?B?QW5uYSBOb3dhaw==?= <anna@contoso.com>
            To: ben@contoso.com
            Subject: =?ISO-8859-2?Q?Zam=F3wienie_przyj=EAte?=
            Date: Wed, 15 Oct 2026 10:00:00 +0200
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="XYZ"

            --XYZ
            Content-Type: text/plain; charset=utf-8
            Content-Transfer-Encoding: quoted-printable

            Dzie=C5=84 dobry,=
             zam=C3=B3wienie jest przyj=C4=99te.
            --XYZ
            Content-Type: application/pdf; name="invoice.pdf"
            Content-Disposition: attachment; filename="invoice.pdf"
            Content-Transfer-Encoding: base64

            JVBERi0=
            --XYZ--
            """;
        var text = Documents.Read("mail.eml", Encoding.UTF8.GetBytes(eml)).Text!.Replace("\r", "");
        Assert.Contains("Subject: Zamówienie przyjęte", text);
        Assert.Contains("From: Anna Nowak <anna@contoso.com>", text);
        Assert.Contains("Attachments: invoice.pdf", text);
        Assert.Contains("Dzień dobry, zamówienie jest przyjęte.", text);
    }

    [Fact]
    public void Zip_lists_its_files_and_reads_one()
    {
        var zip = Zip(("logs/app.log", "ERROR something broke"), ("readme.txt", "hello"));
        var list = Documents.Read("logs.zip", zip);
        Assert.Contains("- logs/app.log", list.Text);
        var one = Documents.Read("logs.zip", zip, part: "app.log");
        Assert.Equal("ERROR something broke", one.Text);
        Assert.StartsWith("logs/app.log from logs.zip", one.Note);
    }

    [Fact]
    public void Long_texts_are_cut_with_an_outline_without_page_markers()
    {
        var text = "## Page 1\n# Goal\nx\n## Page 2\n# Settings\n" + new string('y', 500);
        var cut = Documents.Fit(text, 100, full: false);
        Assert.Contains("outline: Goal | Settings", cut);
        Assert.Contains("detail=full for all", cut);
        Assert.Equal(text, Documents.Fit(text, 100, full: true));
    }

    [Fact]
    public void Saved_files_keep_their_name_inside_the_folder()
    {
        Assert.Equal("evil.exe", Downloads.SafeName(@"..\..\Windows\evil.exe"));
        Assert.Equal("_CON.txt", Downloads.SafeName("CON.txt"));
        Assert.Equal("a_b.txt", Downloads.SafeName("a:b.txt"));
        Assert.Equal("file", Downloads.SafeName("  .. "));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x.log"), "");
        Assert.Equal(Path.Combine(dir, "x (2).log"), Downloads.FreePath(dir, "x.log"));
    }

    [Fact]
    public async Task Saving_streams_to_disk_with_a_checksum()
    {
        var body = new byte[3 * 1024 * 1024];
        new Random(1).NextBytes(body);
        var ado = new FakeAdo().On("/contoso/_apis/wit/attachments/abc", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        var text = await Downloads.SaveAsync(ado.Client(), "_apis/wit/attachments/abc?download=true", dir, "123", @"..\trace.etl", default);
        var path = Path.Combine(dir, "123", "trace.etl");
        Assert.StartsWith($"saved: {path} (3 MB, sha256 ", text);
        Assert.Equal(body, File.ReadAllBytes(path));
        Assert.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant(), text);
        // A second save of the same name does not overwrite.
        await Downloads.SaveAsync(ado.Client(), "_apis/wit/attachments/abc", dir, "123", "trace.etl", default);
        Assert.True(File.Exists(Path.Combine(dir, "123", "trace (2).etl")));
    }
}
