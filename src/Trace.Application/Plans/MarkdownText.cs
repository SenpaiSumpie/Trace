using System.Globalization;
using System.Text;

namespace Trace.Application.Plans;

/// <summary>
/// Puts untrusted text into a Markdown document so that it cannot change the document's
/// structure (ADR-018). Untrusted text is never escaped character by character and left in the
/// flow of the document; it is always <em>quoted</em>, in one of two ways:
/// <list type="bullet">
/// <item><see cref="Code"/>: one line, inside an inline code span;</item>
/// <item><see cref="Block"/>: several lines, inside a fenced code block.</item>
/// </list>
/// Markdown does not interpret anything inside either: no heading, list, table, quote, link,
/// autolink, image, HTML, comment or entity can begin there. The only way out of a code span or
/// a fence is its closing run of backticks, so the delimiter is always chosen longer than any
/// run of backticks in the text. A code span also ends at a blank line, so <see cref="Code"/>
/// never emits a line break.
/// </summary>
public static class MarkdownText
{
    /// <summary>What stands in for text that is empty once cleaned. Written by TRACE, outside any quoting.</summary>
    public const string EmptyPlaceholder = "(empty)";

    private const char Replacement = '\uFFFD';

    /// <summary>
    /// The text as one inline code span on one line.
    /// <para>
    /// Every line break of any kind (CR, LF, vertical tab, form feed, NEL, the Unicode line and
    /// paragraph separators) and every tab becomes a space; other control characters, NUL
    /// included, become U+FFFD; invisible format characters (bidirectional controls, zero-width
    /// space, byte order mark, tag characters) are removed, except the zero-width joiner and
    /// non-joiner that scripts and emoji need.
    /// </para>
    /// </summary>
    public static string Code(string? text)
    {
        var line = Clean(text, keepLines: false);
        if (string.IsNullOrWhiteSpace(line))
        {
            return EmptyPlaceholder;
        }

        var delimiter = new string('`', LongestRun(line, '`') + 1);

        // A span whose content begins or ends with a backtick needs a space between it and the
        // delimiter. CommonMark removes one space from each side when both are there, so a
        // content that itself begins or ends with a space is padded too and comes out unchanged.
        var pad = line[0] is '`' or ' ' || line[^1] is '`' or ' ' ? " " : string.Empty;
        return string.Concat(delimiter, pad, line, pad, delimiter);
    }

    /// <summary>
    /// The text as a fenced code block without an info string, to be placed at the top level of
    /// the document with a blank line before and after. Line breaks of every kind become
    /// <c>\n</c>; tabs are kept; other characters are cleaned as in <see cref="Code"/>. The
    /// fence is made of backticks and is longer than any run of backticks in the text, so no
    /// line of the text can close it; a run of tildes cannot close a backtick fence at all.
    /// </summary>
    public static string Block(string? text)
    {
        // Nothing is trimmed: blank first or last lines are part of what was cited.
        var content = Clean(text, keepLines: true);
        if (string.IsNullOrWhiteSpace(content))
        {
            return EmptyPlaceholder;
        }

        var fence = new string('`', Math.Max(3, LongestRun(content, '`') + 1));
        return string.Concat(fence, "\n", content, "\n", fence);
    }

    private static string Clean(string? text, bool keepLines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder(text.Length);

        // Enumerating runes turns a lone surrogate into U+FFFD, so the result is always valid UTF-16.
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\n' when keepLines && cleaned.Length > 0 && cleaned[^1] == '\r':
                    // CR LF is one line break; the CR already wrote it.
                    cleaned[^1] = '\n';
                    continue;

                case '\r' when keepLines:
                    // Marked so that a following LF is not counted as a second break.
                    cleaned.Append('\r');
                    continue;

                case '\t' when keepLines:
                    cleaned.Append('\t');
                    continue;

                case '\t':
                    AppendBreak(cleaned, keepLines);
                    continue;

                // Zero-width non-joiner and joiner: part of ordinary text in several scripts and in emoji.
                case 0x200C or 0x200D:
                    cleaned.Append((char)rune.Value);
                    continue;
            }

            if (IsLineBreak(rune))
            {
                AppendBreak(cleaned, keepLines);
                continue;
            }

            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Surrogate:
                    cleaned.Append(Replacement);
                    break;

                case UnicodeCategory.Format:
                    // Bidirectional controls, zero-width space, BOM, soft hyphen, tag characters: dropped.
                    break;

                default:
                    cleaned.Append(rune.ToString());
                    break;
            }
        }

        // A CR that was not followed by LF is a line break of its own.
        return cleaned.Replace('\r', '\n').ToString();
    }

    private static bool IsLineBreak(Rune rune) =>
        rune.Value is '\n' or '\r' or 0x0B or 0x0C or 0x85 or 0x2028 or 0x2029;

    private static void AppendBreak(StringBuilder cleaned, bool keepLines)
    {
        if (keepLines)
        {
            cleaned.Append('\n');
        }
        else if (cleaned.Length == 0 || cleaned[^1] != ' ')
        {
            // A line break and whatever blank space preceded it are one space.
            cleaned.Append(' ');
        }
    }

    private static int LongestRun(string text, char character)
    {
        var longest = 0;
        var current = 0;
        foreach (var c in text)
        {
            current = c == character ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }
}
