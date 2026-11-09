using System.Text;
using System.Text.RegularExpressions;

namespace Lockbox.Core.Tools;

/// <summary>
/// A deliberately small Markdown reader for release notes: headings, paragraphs, bullet and numbered
/// lists (nested by indentation), quotes, code blocks, rules, and **bold**, *italic*, `code` and
/// [links](url) inside text. Anything it doesn't understand comes through as plain text, never lost.
/// Links keep only their text: release notes aren't covered by the update signature, so nothing in
/// them should be clickable.
/// </summary>
public static partial class MarkdownLite
{
    public enum BlockKind { Heading, Paragraph, Bullet, Numbered, Quote, Code, Rule }

    /// <param name="Level">Heading level (1–6) or list nesting depth (0 = top level).</param>
    /// <param name="Marker">The number for numbered items ("3."), otherwise empty.</param>
    public sealed record Block(BlockKind Kind, IReadOnlyList<Span> Spans, int Level = 0, string Marker = "")
    {
        public string PlainText => string.Concat(Spans.Select(s => s.Text));
    }

    public sealed record Span(string Text, bool Bold = false, bool Italic = false, bool Code = false);

    public static IReadOnlyList<Block> Parse(string? markdown)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var para = new StringBuilder();
        BlockKind? listKind = null; int listLevel = 0; string listMarker = "";
        var item = new StringBuilder();

        void FlushParagraph()
        {
            if (para.Length > 0) blocks.Add(new Block(BlockKind.Paragraph, ParseInline(para.ToString())));
            para.Clear();
        }
        void FlushItem()
        {
            if (listKind is { } k && item.Length > 0) blocks.Add(new Block(k, ParseInline(item.ToString()), listLevel, listMarker));
            item.Clear();
            listKind = null;
        }
        void FlushAll() { FlushParagraph(); FlushItem(); }

        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i].Replace("\t", "    ");
            string line = raw.Trim();

            if (line.Length == 0) { FlushAll(); continue; }

            if (line.StartsWith("```") || line.StartsWith("~~~"))
            {
                FlushAll();
                string fence = line[..3];
                var code = new List<string>();
                while (++i < lines.Length && !lines[i].Trim().StartsWith(fence)) code.Add(lines[i].TrimEnd());
                blocks.Add(new Block(BlockKind.Code, [new Span(string.Join('\n', code), Code: true)]));
                continue;
            }

            if (RuleRx().IsMatch(line)) { FlushAll(); blocks.Add(new Block(BlockKind.Rule, [])); continue; }

            if (HeadingRx().Match(line) is { Success: true } h)
            {
                FlushAll();
                blocks.Add(new Block(BlockKind.Heading, ParseInline(h.Groups[2].Value.TrimEnd('#', ' ')), h.Groups[1].Length));
                continue;
            }

            int indent = raw.Length - raw.TrimStart().Length;
            if (BulletRx().Match(line) is { Success: true } b)
            {
                FlushAll();
                listKind = BlockKind.Bullet; listLevel = Math.Min(indent / 2, 4); listMarker = "";
                item.Append(b.Groups[1].Value);
                continue;
            }
            if (NumberRx().Match(line) is { Success: true } n)
            {
                FlushAll();
                listKind = BlockKind.Numbered; listLevel = Math.Min(indent / 2, 4); listMarker = n.Groups[1].Value + ".";
                item.Append(n.Groups[2].Value);
                continue;
            }

            if (line.StartsWith('>'))
            {
                FlushAll();
                blocks.Add(new Block(BlockKind.Quote, ParseInline(line.TrimStart('>', ' '))));
                continue;
            }

            // Plain text continues whatever is open: a list item (lazy continuation) or a paragraph.
            if (listKind != null) { item.Append(' ').Append(line); continue; }
            if (para.Length > 0) para.Append(' ');
            para.Append(line);
        }
        FlushAll();
        return blocks;
    }

    /// <summary>Splits text into styled spans. Unmatched markers are kept as literal characters.</summary>
    public static IReadOnlyList<Span> ParseInline(string text)
    {
        var spans = new List<Span>();
        var sb = new StringBuilder();
        bool bold = false, italic = false;

        void Flush()
        {
            if (sb.Length == 0) return;
            if (spans.Count > 0 && spans[^1] is { Code: false } last && last.Bold == bold && last.Italic == italic)
                spans[^1] = last with { Text = last.Text + sb };
            else spans.Add(new Span(sb.ToString(), bold, italic));
            sb.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '\\' && i + 1 < text.Length && "\\`*_[]()#+-.!>".Contains(text[i + 1])) { sb.Append(text[++i]); continue; }

            if (c == '`' && text.IndexOf('`', i + 1) is var end and > 0)
            {
                Flush();
                spans.Add(new Span(text[(i + 1)..end], bold, italic, Code: true));
                i = end;
                continue;
            }

            if (c == '[' && LinkRx().Match(text, i) is { Success: true } link && link.Index == i)
            {
                sb.Append(string.Concat(ParseInline(link.Groups[1].Value).Select(s => s.Text)));
                i += link.Length - 1;
                continue;
            }

            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                // Toggle bold only if a closing pair exists (opening) or we're inside bold (closing).
                if (bold || text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal) > i + 2)
                {
                    Flush(); bold = !bold; i++;
                    continue;
                }
            }
            else if (c == '*' || (c == '_' && IsWordBoundary(text, i)))
            {
                if (italic || HasClosingSingle(text, i, c))
                {
                    Flush(); italic = !italic;
                    continue;
                }
            }

            sb.Append(c);
        }
        Flush();
        return spans;
    }

    // snake_case_words shouldn't turn italic: '_' only counts next to a space or the ends.
    private static bool IsWordBoundary(string s, int i) =>
        i == 0 || i == s.Length - 1 || !char.IsLetterOrDigit(s[i - 1]) || !char.IsLetterOrDigit(s[i + 1]);

    private static bool HasClosingSingle(string s, int i, char c)
    {
        if (i + 1 >= s.Length || char.IsWhiteSpace(s[i + 1])) return false;
        for (int j = i + 2; j < s.Length; j++)
            if (s[j] == c && (j + 1 >= s.Length || s[j + 1] != c) && !char.IsWhiteSpace(s[j - 1])) return true;
        return false;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"^[-*+]\s+(.*)$")] private static partial Regex BulletRx();
    [GeneratedRegex(@"^(\d{1,3})[.)]\s+(.*)$")] private static partial Regex NumberRx();
    [GeneratedRegex(@"^(?:-\s*){3,}$|^(?:\*\s*){3,}$|^(?:_\s*){3,}$")] private static partial Regex RuleRx();
    [GeneratedRegex(@"\[([^\]]*)\]\(([^)\s]*)\)")] private static partial Regex LinkRx();
}
