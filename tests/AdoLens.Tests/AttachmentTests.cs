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
    public async Task All_images_of_an_item_come_in_one_answer()
    {
        var items = new DemoItems();
        var rel = (string id) => new System.Text.Json.Nodes.JsonObject
        {
            ["rel"] = "AttachedFile",
            ["url"] = $"https://dev.azure.com/contoso/_apis/wit/attachments/{id}?fileName=image.png",
            ["attributes"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "image.png", ["resourceSize"] = 9 },
        };
        items.Items[1]["relations"] = new System.Text.Json.Nodes.JsonArray(rel("a1"), rel("a2"), new System.Text.Json.Nodes.JsonObject
        {
            ["rel"] = "AttachedFile", ["url"] = "https://dev.azure.com/contoso/_apis/wit/attachments/a3?fileName=spec.txt",
            ["attributes"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "spec.txt", ["resourceSize"] = 4 },
        });
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0];
        var ado = items.Routes(new DemoProject().Routes(new FakeAdo()))
            .On("/contoso/_apis/wit/attachments/", _ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
        var dir = Path.Combine(Path.GetTempPath(), $"adolens-att-{Guid.NewGuid():N}");
        try
        {
            var s = new Settings { DefaultConnection = "contoso" };
            s.Connections["contoso"] = new Connection { Organization = "contoso", Project = "Demo" };
            var lens = new Lens(s, "0.0.0", dir, _ => null, _ => "pat", _ => ado.Client());

            var (text, images) = await Attachments.ReadAsync(lens, null, 1, "all", null);
            Assert.Contains("2 of 2 file(s)", text);
            Assert.Equal(2, images.Count);

            // Two files with one name: a list of numbers takes both.
            var (_, picked) = await Attachments.ReadAsync(lens, null, 1, "1;2", null);
            Assert.Equal(2, picked.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
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
        Assert.Equal("a;b", Attachments.Extract("x.csv", Encoding.UTF8.GetBytes("\uFEFFa;b")));
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

    /// <summary>A minimal compound file (version 3, 512-byte sectors, no mini stream) with the given top-level streams.</summary>
    static byte[] Msg(params (string Name, byte[] Data)[] streams)
    {
        const uint Free = 0xFFFFFFFF, End = 0xFFFFFFFE;
        var sectors = new List<byte[]>();
        var fat = new List<uint> { 0xFFFFFFFD, 2, End }; // sector 0: the FAT, sectors 1-2: the directory
        var starts = new List<uint>();
        foreach (var (_, data) in streams)
        {
            var count = Math.Max(1, (data.Length + 511) / 512);
            starts.Add((uint)(3 + sectors.Count));
            for (var k = 0; k < count; k++)
            {
                var sec = new byte[512];
                Array.Copy(data, k * 512, sec, 0, Math.Min(512, data.Length - k * 512));
                sectors.Add(sec);
                fat.Add(k == count - 1 ? End : (uint)(fat.Count + 1));
            }
        }
        var header = new byte[512];
        BitConverter.GetBytes(0xE11AB1A1E011CFD0).CopyTo(header, 0);
        BitConverter.GetBytes((ushort)0x3E).CopyTo(header, 0x18);
        BitConverter.GetBytes((ushort)3).CopyTo(header, 0x1A);
        BitConverter.GetBytes((ushort)0xFFFE).CopyTo(header, 0x1C);
        BitConverter.GetBytes((ushort)9).CopyTo(header, 0x1E);
        BitConverter.GetBytes((ushort)6).CopyTo(header, 0x20);
        BitConverter.GetBytes(1u).CopyTo(header, 0x2C);
        BitConverter.GetBytes(1u).CopyTo(header, 0x30);
        BitConverter.GetBytes(0u).CopyTo(header, 0x38); // cutoff 0: every stream in regular sectors
        BitConverter.GetBytes(End).CopyTo(header, 0x3C);
        BitConverter.GetBytes(End).CopyTo(header, 0x44);
        for (var k = 0; k < 109; k++) BitConverter.GetBytes(k == 0 ? 0u : Free).CopyTo(header, 0x4C + k * 4);
        var fatSector = new byte[512];
        for (var k = 0; k < 128; k++) BitConverter.GetBytes(k < fat.Count ? fat[k] : Free).CopyTo(fatSector, k * 4);
        var dir = new byte[1024];
        void Entry(int index, string name, byte type, uint start, uint size, uint right, uint child)
        {
            var o = index * 128;
            var n = Encoding.Unicode.GetBytes(name + "\0");
            n.CopyTo(dir, o);
            BitConverter.GetBytes((ushort)n.Length).CopyTo(dir, o + 64);
            dir[o + 66] = type;
            dir[o + 67] = 1;
            BitConverter.GetBytes(Free).CopyTo(dir, o + 68);
            BitConverter.GetBytes(right).CopyTo(dir, o + 72);
            BitConverter.GetBytes(child).CopyTo(dir, o + 76);
            BitConverter.GetBytes(start).CopyTo(dir, o + 116);
            BitConverter.GetBytes((ulong)size).CopyTo(dir, o + 120);
        }
        Entry(0, "Root Entry", 5, End, 0, Free, 1);
        for (var k = 0; k < streams.Length; k++)
            Entry(k + 1, streams[k].Name, 2, starts[k], (uint)streams[k].Data.Length, k + 2 <= streams.Length ? (uint)(k + 2) : Free, Free);
        return [.. header, .. fatSector, .. dir, .. sectors.SelectMany(x => x)];
    }

    [Fact]
    public void Outlook_messages_are_read_as_text()
    {
        var msg = Msg(
            ("__substg1.0_0037001F", Encoding.Unicode.GetBytes("Re: shelf labels")),
            ("__substg1.0_0C1A001F", Encoding.Unicode.GetBytes("Anna Nowak")),
            ("__substg1.0_0E04001F", Encoding.Unicode.GetBytes("Ben Kowalski")),
            ("__substg1.0_1000001F", Encoding.Unicode.GetBytes("Hi Ben,\r\nyes, switch it off.\r\n" + new string('.', 600))));
        var text = Attachments.Extract("reply.msg", msg)!;
        Assert.StartsWith("Subject: Re: shelf labels\nFrom: Anna Nowak\nTo: Ben Kowalski\n\nHi Ben,\nyes, switch it off.", text.Replace("\r", ""));
        Assert.Null(OutlookMsg.Read([1, 2, 3]));
    }
}
