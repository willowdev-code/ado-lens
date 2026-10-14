using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AdoLens;

/// <summary>
/// Azure DevOps keeps rich text fields and comments as HTML. Claude reads them as compact markdown (no styles, no
/// attachment URLs) and writes markdown, which is turned into the small HTML subset the work item form renders.
/// </summary>
public static partial class Html
{
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"<(/?)([a-zA-Z][a-zA-Z0-9]*)((?:[^>""']|""[^""]*""|'[^']*')*)>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\b(href|src|alt)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex Attribute();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    /// <summary>HTML → markdown: headings, lists, bold, links, code, tables as "a | b"; images become "[image]".</summary>
    public static string ToMarkdown(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = Comment().Replace(ScriptOrStyle().Replace(html, ""), "");
        var sb = new StringBuilder();
        var lists = new Stack<(bool Ordered, int Count)>();
        string? href = null;
        var linkStart = 0;
        var pos = 0;

        foreach (Match m in Tag().Matches(s))
        {
            sb.Append(Text(s[pos..m.Index]));
            pos = m.Index + m.Length;
            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value.ToLowerInvariant();
            var attrs = m.Groups[3].Value;
            switch (name)
            {
                case "br": sb.Append('\n'); break;
                case "p" or "div" or "section" or "article" or "blockquote": sb.Append(closing ? "\n\n" : "\n"); break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    sb.Append(closing ? "\n\n" : "\n\n" + new string('#', name[1] - '0') + " ");
                    break;
                case "ul" or "ol":
                    // Only the outermost list is separated from the text; nested ones continue under their item.
                    if (closing) { if (lists.Count > 0) lists.Pop(); if (lists.Count == 0) sb.Append('\n'); }
                    else { if (lists.Count == 0) sb.Append('\n'); lists.Push((name == "ol", 0)); }
                    break;
                case "li" when !closing:
                    var depth = Math.Max(0, lists.Count - 1);
                    var marker = "- ";
                    if (lists.Count > 0 && lists.Peek().Ordered)
                    {
                        var top = lists.Pop();
                        lists.Push((true, top.Count + 1));
                        marker = $"{top.Count + 1}. ";
                    }
                    sb.Append('\n').Append(new string(' ', depth * 2)).Append(marker);
                    break;
                case "b" or "strong": sb.Append("**"); break;
                case "code" when !InPre(sb): sb.Append('`'); break;
                case "pre": sb.Append(closing ? "\n```\n" : "\n```\n"); break;
                case "tr": sb.Append(closing ? "" : "\n"); break;
                case "td" or "th" when closing: sb.Append(" | "); break;
                case "img" when !closing:
                    var alt = Attr(attrs, "alt");
                    sb.Append(string.IsNullOrWhiteSpace(alt) ? "[image]" : $"[image: {alt}]");
                    break;
                case "a":
                    if (!closing) { href = Attr(attrs, "href"); linkStart = sb.Length; }
                    else if (href is not null)
                    {
                        var text = sb.ToString(linkStart, sb.Length - linkStart).Trim();
                        if (href.StartsWith("http", StringComparison.OrdinalIgnoreCase) && text != href && !text.StartsWith('@'))
                            sb.Append($" ({href})");
                        href = null;
                    }
                    break;
            }
        }
        sb.Append(Text(s[pos..]));
        return Tidy(sb.ToString());
    }

    static bool InPre(StringBuilder sb)
    {
        var t = sb.ToString();
        return (t.Length - t.Replace("```", "").Length) / 3 % 2 == 1;
    }

    static string Text(string raw) => Spaces().Replace(WebUtility.HtmlDecode(raw.Replace('\n', ' ').Replace('\r', ' ')), " ");

    static string? Attr(string attrs, string name)
    {
        foreach (Match a in Attribute().Matches(attrs))
            if (a.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
        return null;
    }

    static string Tidy(string md)
    {
        // Indentation means something only in nested list items and code blocks; elsewhere it is left over from the HTML layout.
        var output = new List<string>();
        var code = false;
        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            if (raw.Trim() == "```") { code = !code; output.Add("```"); continue; }
            var l = code ? raw.TrimEnd() : raw.TrimEnd().TrimEnd('|').TrimEnd();
            output.Add(l.Trim().Length == 0 ? "" : code || IsListItem(l.TrimStart()) ? l : l.TrimStart());
        }
        var text = string.Join('\n', output);
        text = text.Replace("****", "").Replace("** **", " ");
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    static bool IsListItem(string line) =>
        line.StartsWith("- ", StringComparison.Ordinal) || ListItem().IsMatch(line);

    /// <summary>Words in markdown/plain text (for the soft length limits of work item content).</summary>
    public static int Words(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : text.Split((char[])[' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetterOrDigit));

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"\[([^\]]+)\]\((https?://[^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^(\s*)([-*]|\d+\.)\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    /// <summary>
    /// Markdown → HTML for work item fields and comments: paragraphs, headings, nested bullet and numbered lists, bold,
    /// inline code, links, fenced code. Text is HTML-encoded first; <paramref name="raw"/> maps placeholders that must
    /// stay unencoded (mentions) to their HTML.
    /// </summary>
    public static string FromMarkdown(string? markdown, IReadOnlyDictionary<string, string>? raw = null)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var sb = new StringBuilder();
        var paragraph = new List<string>();
        var lists = new Stack<(string Tag, int Indent)>();
        var inCode = false;

        string Inline(string text)
        {
            var h = WebUtility.HtmlEncode(text);
            h = Link().Replace(h, m => $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
            h = Bold().Replace(h, "<b>$1</b>");
            h = InlineCode().Replace(h, "<code>$1</code>");
            if (raw is not null)
                foreach (var (key, value) in raw) h = h.Replace(WebUtility.HtmlEncode(key), value);
            return h;
        }
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            sb.Append("<div>").Append(string.Join("<br>", paragraph.Select(Inline))).Append("</div>");
            paragraph.Clear();
        }
        void CloseLists(int indent = -1)
        {
            while (lists.Count > 0 && lists.Peek().Indent > indent)
                sb.Append($"</li></{lists.Pop().Tag}>");
        }

        foreach (var rawLine in markdown.Replace("\r", "").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.TrimStart().StartsWith("```"))
            {
                FlushParagraph(); CloseLists();
                sb.Append(inCode ? "</pre>" : "<pre>");
                inCode = !inCode;
                continue;
            }
            if (inCode) { sb.Append(WebUtility.HtmlEncode(line)).Append('\n'); continue; }
            if (line.Length == 0) { FlushParagraph(); CloseLists(); continue; }

            if (Heading().Match(line) is { Success: true } h)
            {
                FlushParagraph(); CloseLists();
                var level = Math.Min(h.Groups[1].Length + 1, 6); // "#" becomes h2: the title is the work item's h1
                sb.Append($"<h{level}>{Inline(h.Groups[2].Value)}</h{level}>");
                continue;
            }
            if (ListItem().Match(line) is { Success: true } li)
            {
                FlushParagraph();
                var indent = li.Groups[1].Value.Replace("\t", "  ").Length;
                var tag = char.IsDigit(li.Groups[2].Value[0]) ? "ol" : "ul";
                CloseLists(indent);
                if (lists.Count > 0 && lists.Peek().Indent == indent)
                {
                    if (lists.Peek().Tag != tag) { sb.Append($"</li></{lists.Pop().Tag}><{tag}><li>"); lists.Push((tag, indent)); }
                    else sb.Append("</li><li>");
                }
                else
                {
                    sb.Append($"<{tag}><li>");
                    lists.Push((tag, indent));
                }
                sb.Append(Inline(li.Groups[3].Value));
                continue;
            }
            if (lists.Count > 0 && rawLine.StartsWith("  ")) { sb.Append("<br>").Append(Inline(line.Trim())); continue; }
            CloseLists();
            paragraph.Add(line);
        }
        if (inCode) sb.Append("</pre>");
        FlushParagraph();
        CloseLists();
        return sb.ToString();
    }
}
