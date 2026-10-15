using System.Text;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>A saved e-mail (.eml, MIME): headers, the text body (plain, else HTML as text), the names of its attachments.</summary>
public static partial class Eml
{
    static Eml() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    sealed record Part(Dictionary<string, string> Headers, string Body);

    public static string Read(string raw)
    {
        var root = Parse(raw.Replace("\r\n", "\n"));
        var sb = new StringBuilder();
        void Line(string label, string header) { if (root.Headers.TryGetValue(header, out var v) && v.Length > 0) sb.AppendLine($"{label}: {Words(v)}"); }
        Line("Subject", "subject");
        Line("From", "from");
        Line("To", "to");
        Line("Cc", "cc");
        Line("Sent", "date");
        var leaves = Leaves(root).ToList();
        var files = leaves.Select(FileName).Where(n => n is not null).ToList();
        if (files.Count > 0) sb.AppendLine($"Attachments: {string.Join(", ", files)}");
        sb.AppendLine();
        var plain = leaves.FirstOrDefault(p => FileName(p) is null && ContentType(p).StartsWith("text/plain", StringComparison.OrdinalIgnoreCase));
        var html = leaves.FirstOrDefault(p => FileName(p) is null && ContentType(p).StartsWith("text/html", StringComparison.OrdinalIgnoreCase));
        var body = plain is not null ? Decode(plain) : html is not null ? Html.ToMarkdown(Decode(html)) : null;
        sb.Append(string.IsNullOrWhiteSpace(body) ? "(no text body)" : body.Replace("\r\n", "\n").Trim());
        return sb.ToString();
    }

    static Part Parse(string text)
    {
        var split = text.IndexOf("\n\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var body = split < 0 ? "" : text[(split + 2)..];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        foreach (var line in head.Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && last is not null) headers[last] += " " + line.Trim();
            else if (line.IndexOf(':') is var c and > 0)
            {
                last = line[..c].Trim();
                headers[last] = headers.TryGetValue(last, out var old) ? old : line[(c + 1)..].Trim();
            }
        }
        return new Part(headers, body);
    }

    static string ContentType(Part p) => p.Headers.GetValueOrDefault("content-type") ?? "text/plain";

    static string? Param(string header, string name) =>
        Regex.Match(header, $@"\b{name}\*?=\s*(?:""(?<v>[^""]*)""|(?<v>[^;\s]+))", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups["v"].Value : null;

    static IEnumerable<Part> Leaves(Part p)
    {
        var type = ContentType(p);
        if (!type.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) || Param(type, "boundary") is not { } boundary)
        {
            yield return p;
            yield break;
        }
        var sections = p.Body.Split("--" + boundary);
        foreach (var s in sections.Skip(1))
        {
            if (s.StartsWith("--", StringComparison.Ordinal)) break;
            foreach (var leaf in Leaves(Parse(s.TrimStart('\n'))))
                yield return leaf;
        }
    }

    static string? FileName(Part p)
    {
        var disposition = p.Headers.GetValueOrDefault("content-disposition") ?? "";
        var name = Param(disposition, "filename") ?? Param(ContentType(p), "name");
        if (name is null && !disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase)) return null;
        return name is null ? "(unnamed)" : Words(name);
    }

    static Encoding Charset(string? name)
    {
        try { return name is null ? Encoding.UTF8 : Encoding.GetEncoding(name.Trim('"')); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    static string Decode(Part p)
    {
        var encoding = (p.Headers.GetValueOrDefault("content-transfer-encoding") ?? "").Trim().ToLowerInvariant();
        var charset = Charset(Param(ContentType(p), "charset"));
        // 7bit / 8bit: the body is text already (the file was read as UTF-8).
        return encoding switch
        {
            "base64" => charset.GetString(SafeBase64(p.Body)),
            "quoted-printable" => charset.GetString(QuotedPrintable(p.Body, header: false)),
            _ => p.Body,
        };
    }

    static byte[] SafeBase64(string s)
    {
        try { return Convert.FromBase64String(Regex.Replace(s, @"[^A-Za-z0-9+/=]", "")); }
        catch (FormatException) { return []; }
    }

    static byte[] QuotedPrintable(string s, bool header)
    {
        var bytes = new List<byte>();
        s = Regex.Replace(s, @"=\n", ""); // soft line breaks
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (header && s[i] == '_') bytes.Add((byte)' ');
            else bytes.AddRange(Encoding.Latin1.GetBytes(s[i].ToString()));
        }
        return [.. bytes];
    }

    [GeneratedRegex(@"=\?(?<cs>[^?]+)\?(?<enc>[BbQq])\?(?<text>[^?]*)\?=")]
    private static partial Regex EncodedWord();

    /// <summary>"=?UTF-8?B?…?=" and "=?ISO-8859-2?Q?…?=" in headers.</summary>
    internal static string Words(string header)
    {
        var joined = Regex.Replace(header, @"\?=\s+=\?", "?==?");
        return EncodedWord().Replace(joined, m =>
        {
            var cs = Charset(m.Groups["cs"].Value);
            var bytes = m.Groups["enc"].Value is "B" or "b" ? SafeBase64(m.Groups["text"].Value) : QuotedPrintable(m.Groups["text"].Value, header: true);
            return cs.GetString(bytes);
        }).Trim();
    }
}
