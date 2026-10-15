using System.Text;

namespace AdoLens;

/// <summary>
/// An Outlook e-mail (.msg): a Compound File (CFB) whose top-level streams hold the message properties. Read without
/// any library: subject, sender, recipients, date, the plain-text body (or the HTML one), and the names of the files
/// attached to it.
/// </summary>
public static class OutlookMsg
{
    const uint Free = 0xFFFFFFFF, EndOfChain = 0xFFFFFFFE;

    sealed record Entry(string Name, int Type, uint Start, long Size, uint Left, uint Right, uint Child);

    /// <summary>The compound file: its directory and how to read a stream.</summary>
    sealed class Cfb
    {
        readonly byte[] b;
        readonly int sectorSize, miniSectorSize;
        readonly uint cutoff;
        readonly List<uint> fat = [], miniFat = [];
        public readonly List<Entry> Entries = [];
        byte[] miniStream = [];

        public Cfb(byte[] bytes)
        {
            b = bytes;
            if (b.Length < 512 || BitConverter.ToUInt64(b, 0) != 0xE11AB1A1E011CFD0) throw new InvalidDataException("not a compound file");
            sectorSize = 1 << BitConverter.ToUInt16(b, 0x1E);
            miniSectorSize = 1 << BitConverter.ToUInt16(b, 0x20);
            cutoff = BitConverter.ToUInt32(b, 0x38);
            // The FAT sectors: 109 in the header, the rest in a chain of DIFAT sectors.
            var fatSectors = new List<uint>();
            for (var i = 0; i < 109; i++) fatSectors.Add(BitConverter.ToUInt32(b, 0x4C + i * 4));
            var difat = BitConverter.ToUInt32(b, 0x44);
            for (var guard = 0; difat != EndOfChain && difat != Free && guard < 10000; guard++)
            {
                var off = Offset(difat);
                for (var i = 0; i < sectorSize / 4 - 1; i++) fatSectors.Add(BitConverter.ToUInt32(b, off + i * 4));
                difat = BitConverter.ToUInt32(b, off + sectorSize - 4);
            }
            foreach (var s in fatSectors.Where(s => s != Free && s != EndOfChain))
            {
                var off = Offset(s);
                for (var i = 0; i < sectorSize / 4 && off + i * 4 + 4 <= b.Length; i++) fat.Add(BitConverter.ToUInt32(b, off + i * 4));
            }
            var dir = Chain(BitConverter.ToUInt32(b, 0x30));
            for (var i = 0; i + 128 <= dir.Length; i += 128)
            {
                var nameLength = BitConverter.ToUInt16(dir, i + 64);
                var name = nameLength >= 2 ? Encoding.Unicode.GetString(dir, i, Math.Min(64, (int)nameLength) - 2) : "";
                Entries.Add(new Entry(name, dir[i + 66], BitConverter.ToUInt32(dir, i + 116), (long)BitConverter.ToUInt64(dir, i + 120) & 0xFFFFFFFF,
                    BitConverter.ToUInt32(dir, i + 68), BitConverter.ToUInt32(dir, i + 72), BitConverter.ToUInt32(dir, i + 76)));
            }
            if (Entries.Count > 0)
            {
                miniStream = Chain(Entries[0].Start);
                var mf = Chain(BitConverter.ToUInt32(b, 0x3C));
                for (var i = 0; i + 4 <= mf.Length; i += 4) miniFat.Add(BitConverter.ToUInt32(mf, i));
            }
        }

        int Offset(uint sector) => (int)((sector + 1) * (uint)sectorSize);

        byte[] Chain(uint start)
        {
            if (start == EndOfChain || start == Free) return [];
            using var ms = new MemoryStream();
            for (var (s, guard) = (start, 0); s != EndOfChain && s < fat.Count && guard < 1_000_000; s = fat[(int)s], guard++)
            {
                var off = Offset(s);
                if (off >= b.Length) break;
                ms.Write(b, off, Math.Min(sectorSize, b.Length - off));
            }
            return ms.ToArray();
        }

        public byte[] Read(Entry e)
        {
            if (e.Size < cutoff)
            {
                using var ms = new MemoryStream();
                for (var (s, guard) = (e.Start, 0); s != EndOfChain && s < miniFat.Count && guard < 1_000_000; s = miniFat[(int)s], guard++)
                {
                    var off = (int)s * miniSectorSize;
                    if (off >= miniStream.Length) break;
                    ms.Write(miniStream, off, Math.Min(miniSectorSize, miniStream.Length - off));
                }
                return ms.ToArray()[..(int)Math.Min(e.Size, ms.Length)];
            }
            var all = Chain(e.Start);
            return all[..(int)Math.Min(e.Size, all.Length)];
        }

        /// <summary>The entries directly inside a storage: the red-black tree under its child.</summary>
        public List<Entry> Children(Entry storage)
        {
            var list = new List<Entry>();
            var stack = new Stack<uint>();
            if (storage.Child != Free) stack.Push(storage.Child);
            while (stack.Count > 0 && list.Count < 10000)
            {
                var i = stack.Pop();
                if (i >= Entries.Count) continue;
                var e = Entries[(int)i];
                list.Add(e);
                if (e.Left != Free) stack.Push(e.Left);
                if (e.Right != Free) stack.Push(e.Right);
            }
            return list;
        }
    }

    /// <summary>Whether a compound file is an Outlook item (it has the message property streams at the top).</summary>
    public static bool IsMessage(byte[] bytes)
    {
        try
        {
            var cfb = new Cfb(bytes);
            return cfb.Entries.Count > 0 && cfb.Children(cfb.Entries[0]).Any(e => e.Name.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase)
                                                                                   || e.Name == "__properties_version1.0");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return false; }
    }

    /// <summary>The e-mail as text, or null when the file is not an Outlook message.</summary>
    public static string? Read(byte[] bytes)
    {
        Cfb cfb;
        try { cfb = new Cfb(bytes); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
        if (cfb.Entries.Count == 0) return null;
        var top = cfb.Children(cfb.Entries[0]);
        string? Text(IEnumerable<Entry> scope, int id)
        {
            var hex = id.ToString("X4");
            var unicode = scope.FirstOrDefault(e => e.Name.Equals($"__substg1.0_{hex}001F", StringComparison.OrdinalIgnoreCase));
            if (unicode is not null) return Encoding.Unicode.GetString(cfb.Read(unicode)).TrimEnd('\0');
            var ansi = scope.FirstOrDefault(e => e.Name.Equals($"__substg1.0_{hex}001E", StringComparison.OrdinalIgnoreCase));
            return ansi is null ? null : Encoding.Latin1.GetString(cfb.Read(ansi)).TrimEnd('\0');
        }
        var sb = new StringBuilder();
        void Line(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine($"{label}: {value.Trim()}"); }
        Line("Subject", Text(top, 0x0037));
        var from = Text(top, 0x0C1A);
        var fromMail = Text(top, 0x5D01) ?? Text(top, 0x0C1F);
        Line("From", from is null ? fromMail : fromMail is null || !fromMail.Contains('@') ? from : $"{from} <{fromMail}>");
        Line("To", Text(top, 0x0E04));
        Line("Cc", Text(top, 0x0E03));
        if (SentAt(cfb, top) is { } sent) Line("Sent", sent.ToString("yyyy-MM-dd HH:mm"));
        var attachments = top.Where(e => e.Type == 1 && e.Name.StartsWith("__attach_version1.0_", StringComparison.OrdinalIgnoreCase))
            .Select(a => cfb.Children(a)).Select(c => Text(c, 0x3707) ?? Text(c, 0x3704)).Where(n => n is not null).ToList();
        if (attachments.Count > 0) Line("Attachments", string.Join(", ", attachments));
        sb.AppendLine();
        var body = Text(top, 0x1000);
        if (string.IsNullOrWhiteSpace(body) && top.FirstOrDefault(e => e.Name.Equals("__substg1.0_10130102", StringComparison.OrdinalIgnoreCase)) is { } html)
            body = Html.ToMarkdown(Encoding.UTF8.GetString(cfb.Read(html)));
        sb.Append(string.IsNullOrWhiteSpace(body) ? "(no text body - the message may be in RTF only)" : body.Replace("\r\n", "\n").Trim());
        return sb.ToString();
    }

    /// <summary>PR_CLIENT_SUBMIT_TIME (0x0039) from the top-level property stream.</summary>
    static DateTime? SentAt(Cfb cfb, List<Entry> top)
    {
        var props = top.FirstOrDefault(e => e.Name == "__properties_version1.0");
        if (props is null) return null;
        var data = cfb.Read(props);
        // The top level has a 32-byte header, then 16-byte entries: type (2), id (2), flags (4), value (8).
        for (var i = 32; i + 16 <= data.Length; i += 16)
        {
            if (BitConverter.ToUInt16(data, i) == 0x0040 && BitConverter.ToUInt16(data, i + 2) is 0x0039 or 0x0E06)
            {
                var ft = BitConverter.ToInt64(data, i + 8);
                try { return DateTime.FromFileTimeUtc(ft).ToLocalTime(); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
        }
        return null;
    }
}
