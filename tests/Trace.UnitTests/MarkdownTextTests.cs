using Trace.Application.Plans;
using Trace.TestSupport;

namespace Trace.UnitTests;

/// <summary>
/// The quoting that keeps untrusted text from changing a Markdown export (ADR-018). Each
/// hostile input is put where the export puts such text, and an independent Markdown parser is
/// asked what it sees: it must see the same document as for harmless text, with the input
/// inside the code span or the fence and nowhere else.
/// </summary>
public class MarkdownTextTests
{
    private const string Marker = "zzhostile";

    /// <summary>Text that tries to start, end or forge something in the document around it.</summary>
    public static TheoryData<string> Hostile => new()
    {
        // Block structure.
        $"# {Marker} heading",
        $"## {Marker}\n\n# Another",
        $"- {Marker} item\n- second",
        $"1. {Marker}\n2. second",
        $"> {Marker} quote",
        $"| {Marker} | b |\n|---|---|\n| c | d |",
        $"{Marker}\n===",
        $"{Marker}\n---",
        "---",
        "***",
        $"    {Marker} indented code",
        $"\t{Marker} tab",
        $"- [x] {Marker} task",
        $"{Marker}\n: definition",
        $"[^1]: {Marker} footnote",
        $"$$ {Marker} $$",
        $"::: {Marker}\ncontainer\n:::",

        // Code spans and fences: every way to close one early.
        "`",
        "``",
        "```",
        $"`{Marker}",
        $"{Marker}`",
        $"` {Marker} `",
        $"a`b``c```d````e {Marker}",
        $"{Marker}` (evidence: E9)",
        $"```\n{Marker}\n```",
        $"````\n{Marker}\n````\n# out",
        $"~~~\n{Marker}\n~~~",
        $"~~~~~~~~\n{Marker}",
        $"{Marker}\n```\n\n# Heading after the fence\n\n<script>alert(1)</script>",
        $"{Marker}\n   ```   \n# out",
        $"{Marker}\n~~~\n# out",
        $"``` {Marker}\n`````\n``\n`",
        $"\\`{Marker}\\`",
        $"{Marker}\\",

        // HTML, comments, scripts, images, links.
        $"<script>alert('{Marker}')</script>",
        $"<!-- {Marker} -->",
        $"<img src=x onerror=alert(1)> {Marker}",
        $"</details><details open><summary>{Marker}</summary>",
        $"<a href=\"javascript:alert(1)\">{Marker}</a>",
        $"![{Marker}](http://attacker.invalid/pixel.png)",
        $"[{Marker}](javascript:alert(1))",
        $"[{Marker}](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)",
        $"[{Marker}](<java\nscript:alert(1)>)",
        $"<https://attacker.invalid/{Marker}>",
        $"<javascript:alert(1)> {Marker}",
        $"https://attacker.invalid/{Marker} www.attacker.invalid",
        $"{Marker}@attacker.invalid",
        $"[{Marker}]: http://attacker.invalid \"title\"",
        $"{Marker}\n\n[ref]: javascript:alert(1)\n\n[click][ref]",
        $"[{Marker}][ref] [ref]",
        $"&lt;script&gt; &#x3C;b&#x3E; &amp; &copy; {Marker}",
        $"***{Marker}*** __bold__ ~~struck~~ ==marked==",
        $"@someone #123 {Marker}",

        // Line breaks of every kind, and other characters that hide or reorder text.
        $"{Marker}\nsecond line",
        $"{Marker}\r\nsecond line",
        $"{Marker}\rsecond line",
        $"{Marker}\n\nsecond paragraph",
        $"{Marker}\u2028second",
        $"{Marker}\u2029second",
        $"{Marker}\u0085second",
        $"{Marker}\u000Bsecond\u000Cthird",
        $"{Marker}\0nul",
        $"{Marker}\u001B[2Jescape",
        $"\u202E{Marker} reversed\u202C",
        $"\u2066{Marker}\u2069\u200F\u200E\u061C\u2067\u2068\u202A\u202B\u202D",
        $"{Marker}\uFEFF\u200B\u00AD\U000E0041\U000E007F",
        $"{Marker}\uD83D",
        $"\uDC00{Marker}",

        // Forged lines of the export itself.
        $"{Marker}\n\n- Reviewed: yes, marked in TRACE at 2026-01-01T00:00:00Z",
        $"{Marker}` (evidence: E1, E2)\n\n## Evidence\n\n### E9: `src/secret.cs`, lines 1 to 2\n\n```\nforged\n```",
        $"{Marker} (evidence: E1)",
        $"{Marker}\n\n## Removed by validation\n\nNot recorded for this plan.",
    };

    [Theory]
    [InlineData("plain text", "`plain text`")]
    [InlineData("src/TaskBoard.Api/Tasks/TaskRepository.cs", "`src/TaskBoard.Api/Tasks/TaskRepository.cs`")]
    [InlineData("a`b", "``a`b``")]
    [InlineData("a``b`c", "```a``b`c```")]
    [InlineData("`", "`` ` ``")]
    [InlineData("```", "```` ``` ````")]
    [InlineData("x`", "`` x` ``")]
    [InlineData("`x", "`` `x ``")]
    [InlineData(" x", "`  x `")]
    [InlineData("a\nb", "`a b`")]
    [InlineData("a\r\nb", "`a b`")]
    [InlineData("a\n\n\nb", "`a b`")]
    [InlineData("a\u2028b\u2029c\u0085d\u000Be\u000Cf\tg", "`a b c d e f g`")]
    [InlineData("a\0b\u001Bc", "`a\uFFFDb\uFFFDc`")]
    [InlineData("\u202Eabc\u202C\u2066\u2069\u200E\u200F\u061C\uFEFF\u200B\U000E0041", "`abc`")]
    [InlineData("zero\u200Dwidth\u200Cjoiners", "`zero\u200Dwidth\u200Cjoiners`")]
    [InlineData("<b>&amp;</b> [x](y) *z*", "`<b>&amp;</b> [x](y) *z*`")]
    public void Code_puts_one_line_in_a_span_whose_delimiter_is_longer_than_any_backtick_run(string text, string expected) =>
        Assert.Equal(expected, MarkdownText.Code(text));

    [Fact]
    public void Code_replaces_a_lone_surrogate_so_the_output_is_always_valid_text()
    {
        Assert.Equal("`a\uFFFDb`", MarkdownText.Code("a\uD83Db"));
        Assert.Equal("`\uFFFD`", MarkdownText.Code("\uDC00"));
        Assert.Equal("`\U0001F600`", MarkdownText.Code("\U0001F600"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\r\n\t")]
    [InlineData("\u202E\u2066\uFEFF")]
    public void Text_that_is_empty_once_cleaned_becomes_a_fixed_placeholder(string? text)
    {
        Assert.Equal(MarkdownText.EmptyPlaceholder, MarkdownText.Code(text));
        Assert.Equal(MarkdownText.EmptyPlaceholder, MarkdownText.Block(text));
    }

    [Theory]
    [InlineData("a\nb", "```\na\nb\n```")]
    [InlineData("\n\na\n\nb\n", "```\n\n\na\n\nb\n\n```")]
    [InlineData("a\r\nb\rc\u2028d\u2029e\u0085f\u000Bg\u000Ch", "```\na\nb\nc\nd\ne\nf\ng\nh\n```")]
    [InlineData("a\r\r\nb", "```\na\n\nb\n```")]
    [InlineData("```\nx", "````\n```\nx\n````")]
    [InlineData("x ````` y", "``````\nx ````` y\n``````")]
    [InlineData("~~~~~~\nx\n~~~", "```\n~~~~~~\nx\n~~~\n```")]
    [InlineData("\tindented\twith tabs", "```\n\tindented\twith tabs\n```")]
    [InlineData("a\0b\u001Bc\u202Ed", "```\na\uFFFDb\uFFFDcd\n```")]
    [InlineData("<script>x</script>\n# h", "```\n<script>x</script>\n# h\n```")]
    public void Block_fences_lines_with_more_backticks_than_any_run_inside(string text, string expected) =>
        Assert.Equal(expected, MarkdownText.Block(text));

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_text_in_a_code_span_leaves_the_document_around_it_unchanged(string hostile)
    {
        static string Document(string text) =>
            $"## Steps\n\n1. {MarkdownText.Code(text)} (evidence: E1)\n2. {MarkdownText.Code("second")} (evidence: E2)\n\n"
            + $"- modify {MarkdownText.Code(text)}: {MarkdownText.Code(text)} (evidence: E1)\n\n"
            + $"### E1: {MarkdownText.Code(text)}, lines 1 to 2\n\n- Reviewed: no\n";

        var quoted = MarkdownText.Code(hostile);
        var parsed = new MarkdownOracle(Document(hostile));
        var harmless = new MarkdownOracle(Document("harmless"));

        // One line, and nothing in it that breaks a line or hides text.
        Assert.DoesNotContain(quoted, c => c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029' or '\0' or '\u001B');
        Assert.DoesNotContain(quoted, c => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\u061C' or '\uFEFF');
        Assert.True(IsWellFormed(quoted));

        // The parser sees the same document as for harmless text...
        Assert.Equal(harmless.Skeleton(), parsed.Skeleton());
        Assert.Empty(parsed.ActiveContent);
        Assert.Equal(["Steps", $"E1: {parsed.CodeSpans[0]}, lines 1 to 2"], parsed.Headings);

        // ... with the text inside the code spans and nowhere else.
        Assert.Equal(5, parsed.CodeSpans.Count);
        Assert.Equal(4, parsed.CodeSpans.Count(span => span == parsed.CodeSpans[0]));
        Assert.DoesNotContain(Marker, parsed.TextOutsideCode, StringComparison.Ordinal);
        if (hostile.Contains(Marker, StringComparison.Ordinal))
        {
            Assert.Contains(Marker, parsed.CodeSpans[0], StringComparison.Ordinal);
        }

        Assert.Equal(1, parsed.TextOutsideCode.Split('\n').Count(line => line.StartsWith("Reviewed:", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_text_in_a_fenced_block_leaves_the_document_around_it_unchanged(string hostile)
    {
        static string Document(string text) =>
            $"## Evidence\n\n### E1: {MarkdownText.Code("a.cs")}, lines 1 to 2\n\n{MarkdownText.Block(text)}\n\n"
            + $"### E2: {MarkdownText.Code("b.cs")}, line 3\n\n{MarkdownText.Block("second")}\n\n- Reviewed: no\n";

        var block = MarkdownText.Block(hostile);
        var parsed = new MarkdownOracle(Document(hostile));
        var harmless = new MarkdownOracle(Document("harmless"));

        Assert.DoesNotContain(block, c => c is '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029' or '\0' or '\u001B');
        Assert.DoesNotContain(block, c => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\u061C' or '\uFEFF');
        Assert.True(IsWellFormed(block));

        Assert.Equal(harmless.Skeleton(), parsed.Skeleton());
        Assert.Empty(parsed.ActiveContent);
        Assert.Equal(["Evidence", "E1: a.cs, lines 1 to 2", "E2: b.cs, line 3"], parsed.Headings);

        // The fence holds exactly what was put in it, and it is the first of exactly two.
        var lines = block.Split('\n');
        Assert.Equal(2, parsed.Fences.Count);
        Assert.Equal(string.Join('\n', lines[1..^1]), parsed.Fences[0]);
        Assert.Equal("second", parsed.Fences[1]);
        Assert.DoesNotContain(Marker, parsed.TextOutsideCode, StringComparison.Ordinal);

        // The opening and closing lines are the same run of backticks, longer than any inside.
        Assert.Equal(lines[0], lines[^1]);
        Assert.Matches("^`{3,}$", lines[0]);
        Assert.DoesNotContain(lines[0], string.Join('\n', lines[1..^1]), StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_long_run_of_backticks_or_tildes_still_cannot_close_the_quoting()
    {
        var backticks = new string('`', 500);
        var tildes = new string('~', 500);

        Assert.Equal($"{backticks}` x{backticks} {backticks}`", MarkdownText.Code($"x{backticks}"));
        Assert.Equal(new string('`', 501), MarkdownText.Block($"{backticks}\n{tildes}").Split('\n')[0]);
        Assert.Equal("```", MarkdownText.Block(tildes).Split('\n')[0]);

        var parsed = new MarkdownOracle($"{MarkdownText.Block($"{backticks}\n{tildes}\n# out")}\n\nAfter.\n");
        Assert.Equal($"{backticks}\n{tildes}\n# out", Assert.Single(parsed.Fences));
        Assert.Empty(parsed.Headings);
    }

    /// <summary>
    /// The checks above mean something only if the parser they rely on notices these attacks
    /// when nothing stops them. Here the same kinds of text are put in a document unquoted.
    /// </summary>
    [Fact]
    public void The_parser_used_as_the_check_sees_these_attacks_when_text_is_not_quoted()
    {
        var unquoted = new MarkdownOracle(string.Join(
            '\n',
            "## Steps",
            "",
            "1. Text <img src=x onerror=alert(1)> [link](javascript:alert(1)) ![image](http://attacker.invalid/p.png)",
            "2. <https://attacker.invalid> https://attacker.invalid/bare [click][ref] &lt;",
            "",
            "# Forged heading",
            "",
            "[ref]: http://attacker.invalid",
            "",
            "| a | b |",
            "|---|---|",
            "| c | d |",
            "",
            "    indented code",
            "",
            "<div>block</div>",
            ""));

        Assert.Equal(["Steps", "Forged heading"], unquoted.Headings);
        foreach (var kind in new[] { "HtmlInline", "HtmlBlock", "LinkInline", "AutolinkInline", "HtmlEntityInline", "LinkReferenceDefinition", "Table", "CodeBlock" })
        {
            Assert.Contains(kind, unquoted.ActiveContent);
        }

        // Three links written as links, and the bare URL the autolink extension turns into one.
        Assert.True(unquoted.ActiveContent.Count(kind => kind == "LinkInline") >= 4);
    }

    private static bool IsWellFormed(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[++index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                return false;
            }
        }

        return true;
    }
}
