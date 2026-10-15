using System.IO.Compression;
using System.Text;

namespace AdoLens.Tests;

public class AttachmentTests
{
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

    [Fact]
    public void Word_documents_become_markdown_like_text()
    {
        var docx = Zip(("word/document.xml", """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
              <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Goal</w:t></w:r></w:p>
              <w:p><w:r><w:t>Stores consume </w:t></w:r><w:r><w:t>goods at once.</w:t></w:r></w:p>
              <w:p><w:pPr><w:numPr/></w:pPr><w:r><w:t>first point</w:t></w:r></w:p>
              <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Field</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Value</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """));
        var text = Attachments.Extract("spec.docx", docx)!.Replace("\r", "");
        Assert.Contains("# Goal\nStores consume goods at once.\n- first point", text);
        Assert.Contains("| Field | Value |", text);
    }

    [Fact]
    public void Excel_sheets_become_rows()
    {
        var xlsx = Zip(
            ("xl/sharedStrings.xml", """<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Item</t></si><si><t>Qty</t></si></sst>"""),
            ("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row><c t="s"><v>0</v></c><c t="s"><v>1</v></c></row>
                  <row><c t="inlineStr"><is><t>A-1</t></is></c><c><v>12</v></c></row>
                </sheetData></worksheet>
                """));
        var text = Attachments.Extract("list.xlsx", xlsx)!.Replace("\r", "");
        Assert.Contains("## sheet1\n| Item | Qty |\n| A-1 | 12 |", text);
    }

    [Fact]
    public void Text_files_are_read_and_binary_ones_are_not()
    {
        Assert.Equal("a;b", Attachments.Extract("x.csv", Encoding.UTF8.GetBytes("﻿a;b")));
        Assert.Null(Attachments.Extract("shot.png", [0x89, 0x50, 0x4E, 0x47, 0, 0, 0]));
        Assert.Null(Attachments.Extract("broken.docx", [1, 2, 3]));
    }

    [Fact]
    public void Images_are_recognized_by_their_first_bytes()
    {
        Assert.Equal("image/png", Attachments.ImageMime([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.Equal("image/jpeg", Attachments.ImageMime([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(Attachments.ImageMime(Encoding.UTF8.GetBytes("plain text here")));
    }
}
