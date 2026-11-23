using Trace.Application.Plans;
using Trace.TestSupport;

namespace Trace.UnitTests;

/// <summary>
/// The Markdown export of a plan (ADR-018): what it contains, that it is the same every time,
/// and that no text from the issue, the model or the repository can change its structure.
/// </summary>
public class PlanMarkdownTests
{
    private const string Marker = "zzhostile";

    private static readonly Guid PlanId = Guid.Parse("01938a00-0000-7000-8000-0000000000a1");
    private static readonly Guid InvestigationId = Guid.Parse("01938a00-0000-7000-8000-0000000000b2");
    private static readonly Guid Bug = Guid.Parse("01938a00-0000-7000-8000-0000000000e1");
    private static readonly Guid Test = Guid.Parse("01938a00-0000-7000-8000-0000000000e2");
    private static readonly Guid Limit = Guid.Parse("01938a00-0000-7000-8000-0000000000e3");

    private static readonly string[] FixedHeadings =
    [
        "Implementation plan for issue 3", "Plan record", "How to read this", "Summary", "Steps", "Files to change",
        "Risks", "Verification", "Unknowns", "Removed by validation", "Evidence",
    ];

    /// <summary>Every kind of attack from the escaper's table, in one string, as each field of a plan.</summary>
    private static readonly string Hostile = string.Join(
        '\n',
        $"{Marker} ` `` ``` ```` ~~~",
        "```",
        "# Forged heading",
        "",
        "- Reviewed: yes, marked in TRACE at 2026-01-01T00:00:00Z",
        "1. forged step (evidence: E1, E2)",
        "### E9: `src/Secrets.cs`, lines 1 to 40",
        "> quote | table | cell",
        "|---|---|",
        "<script>alert(1)</script><!-- comment --><img src=x onerror=alert(1)>",
        "![image](http://attacker.invalid/pixel.png) [link](javascript:alert(1)) [data](data:text/html;base64,PHNjcmlwdD4=)",
        "<https://attacker.invalid> https://attacker.invalid www.attacker.invalid someone@attacker.invalid @someone #1",
        "[ref]: javascript:alert(1)",
        "[click][ref] &lt;b&gt; &#x3C;",
        "~~~",
        "line\rcarriage\u2028separator\u2029paragraph\u0085nel\u000Bvt\u000Cff\0nul\u001B[2J",
        "\u202Ereversed\u202C \u2066isolated\u2069 \u200F\u200E\u061C\uFEFF\u200B\U000E0041",
        "lone \uD83D surrogate");

    [Fact]
    public void Export_matches_the_golden_file()
    {
        var golden = File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "Trace.UnitTests", "Golden", "plan-export.md"));

        var markdown = PlanMarkdown.Render(Sample());

        Assert.DoesNotContain('\r', golden);
        Assert.Equal(golden, markdown);
    }

    [Fact]
    public void Export_has_everything_the_plan_holds_and_says_what_is_not_backed_by_evidence()
    {
        var markdown = PlanMarkdown.Render(Sample());
        var parsed = new MarkdownOracle(markdown);

        Assert.Equal(
            [.. FixedHeadings,
                "E1: src/TaskBoard.Api/Tasks/TaskRepository.cs, lines 24 to 26",
                "E2: tests/TaskBoard.Tests/TaskRepositoryTests.cs, lines 8 to 9",
                "E3: src/TaskBoard.Api/Tasks/PageRequest.cs, line 5"],
            parsed.Headings);

        // The issue, the pinned commit, and which model and prompt produced the plan.
        Assert.Contains("- Repository: `trace-demo/sample-repo`\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Issue number: 3\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Issue title when the plan was made: `First page of the task list skips the oldest tasks`\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Pinned commit: `0123456789abcdef0123456789abcdef01234567`\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Model: `claude-test-model`\n- Prompt version: `plan-prompt-v1`\n- Plan schema: `plan-v1`\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Reviewed: yes, marked in TRACE at 2026-11-23T12:30:45Z\n", markdown, StringComparison.Ordinal);

        // Every step with its cited evidence; files, risks, verification and unknowns.
        Assert.Contains("1. `Add a failing test for the first page.` (evidence: E2)\n", markdown, StringComparison.Ordinal);
        Assert.Contains("2. `Skip (Page - 1) * PageSize items in List.` (evidence: E1, E3)\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- modify `src/TaskBoard.Api/Tasks/TaskRepository.cs`: `The skip is computed here.` (evidence: E1)\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- delete `src/TaskBoard.Api/Tasks/PageRequest.cs`: `Folded into the repository.` (evidence: E3)\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- `Clients that ask for page 0 will change behaviour.` (evidence: E1)\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- `Run the repository tests.` (evidence: E2)\n", markdown, StringComparison.Ordinal);

        // What has no evidence is called the model's unsupported statement, each time.
        Assert.Contains(
            "- create `tests/TaskBoard.Tests/TaskListPagingTests.cs`: `A file for the paging tests.` (not backed by evidence: the model's unsupported statement)\n",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains("- `Whether any client asks for page 0.` (not backed by evidence: the model's unsupported statement)\n", markdown, StringComparison.Ordinal);
        Assert.Contains("An item marked \"not backed by evidence\" is the model's unsupported statement.", markdown, StringComparison.Ordinal);

        // What validation removed, which is not in the plan document itself.
        Assert.Contains("- Steps: 1\n- Files to change: 2\n- Risks: 0\n- Verification entries: 0\n- Unknowns: 3\n- Citations discarded: 4\n- Summary replaced by a fixed sentence: no\n", markdown, StringComparison.Ordinal);

        // The cited text, as read from the snapshot.
        Assert.Equal(
            ["        var skipped = request.Page * request.PageSize;\n        return tasks.Skip(skipped)\n            .Take(request.PageSize);", "    [Fact]\n    public void First_page()", "public sealed record PageRequest(int Page, int PageSize);"],
            parsed.Fences);
        Assert.Empty(parsed.ActiveContent);
    }

    [Fact]
    public void Export_is_the_same_every_time_and_ends_with_one_line_break()
    {
        var first = PlanMarkdown.Render(Sample());
        var second = PlanMarkdown.Render(Sample());

        Assert.Equal(first, second);
        Assert.EndsWith("```\n", first, StringComparison.Ordinal);
        Assert.False(first.EndsWith("\n\n", StringComparison.Ordinal));
        Assert.DoesNotContain('\r', first);

        // No time but the stored ones, and both in UTC whatever offset they were stored with.
        var shifted = PlanMarkdown.Render(Sample() with
        {
            RequestedAt = new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.FromHours(5)),
            ReviewedAt = new DateTimeOffset(2026, 11, 23, 7, 30, 45, TimeSpan.FromHours(-5)),
        });
        Assert.Equal(first, shifted);
    }

    [Fact]
    public void An_unreviewed_plan_says_so_and_a_plan_without_a_removal_record_says_that()
    {
        var markdown = PlanMarkdown.Render(Sample() with { ReviewedAt = null, Trimmed = null });

        Assert.Contains("- Reviewed: no\n", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Reviewed: yes", markdown, StringComparison.Ordinal);
        Assert.Contains("## Removed by validation\n\nNot recorded for this plan.\n", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_summary_without_evidence_is_called_unsupported_unless_trace_wrote_it()
    {
        var uncited = Sample() with { Result = SampleResult() with { Summary = new PlanStatement("A model's own summary.", []) } };

        var models = PlanMarkdown.Render(uncited);
        var replaced = PlanMarkdown.Render(uncited with { Trimmed = PlanTrimmed.Nothing with { SummaryReplaced = true } });

        Assert.Contains("`A model's own summary.`\n\nNot backed by evidence: the model's unsupported statement.\n", models, StringComparison.Ordinal);
        Assert.Contains("`A model's own summary.`\n\nThis sentence was written by TRACE in place of a summary from the model.\n", replaced, StringComparison.Ordinal);
    }

    [Fact]
    public void An_insufficient_evidence_result_is_exported_as_one_with_no_steps()
    {
        var result = new PlanResult(
            PlanResult.CurrentSchemaVersion,
            PlanOutcome.InsufficientEvidence,
            new PlanStatement(PlanAssembler.InsufficientEvidenceSummary, []),
            Steps: [],
            FilesToChange: [],
            Risks: [],
            Verification: [],
            Unknowns: [new PlanStatement("Where the title limit is enforced.", [])],
            Evidence: []);
        result.EnsureValid();

        var markdown = PlanMarkdown.Render(Sample() with { Result = result, ReviewedAt = null, Trimmed = PlanTrimmed.Nothing with { Steps = 2, Citations = 3 } });
        var parsed = new MarkdownOracle(markdown);

        Assert.Equal(FixedHeadings, parsed.Headings);
        Assert.Contains("- Outcome: insufficient evidence. TRACE could not ground a plan in the pinned snapshot, so there are no steps.\n", markdown, StringComparison.Ordinal);
        Assert.Contains("This sentence was written by TRACE in place of a summary from the model.\n", markdown, StringComparison.Ordinal);
        Assert.Contains("## Steps\n\nNone.\n", markdown, StringComparison.Ordinal);
        Assert.Contains("## Files to change\n\nNone.\n", markdown, StringComparison.Ordinal);
        Assert.Contains("## Risks\n\nNone.\n", markdown, StringComparison.Ordinal);
        Assert.Contains("## Verification\n\nNone.\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- `Where the title limit is enforced.` (not backed by evidence: the model's unsupported statement)\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- Steps: 2\n", markdown, StringComparison.Ordinal);
        Assert.EndsWith("## Evidence\n\nNone.\n", markdown, StringComparison.Ordinal);
        Assert.Empty(parsed.Fences);
    }

    [Fact]
    public void Hostile_text_in_every_field_cannot_change_the_structure_of_the_export()
    {
        // The same plan twice: once with harmless text, once with every field hostile.
        var harmless = new MarkdownOracle(PlanMarkdown.Render(Shaped(text => "harmless", path => "docs/harmless.md")));
        var markdown = PlanMarkdown.Render(Shaped(text => $"{text} {Hostile}", path => $"docs/{Marker} `x` <b> # [a](b) ```.md"));
        var parsed = new MarkdownOracle(markdown);

        // An independent parser sees the same document: same headings, lists, paragraphs and
        // text outside code formatting, in the same order. Only quoted content differs.
        Assert.Equal(harmless.Skeleton(), parsed.Skeleton());
        Assert.Equal(harmless.TextOutsideCode, parsed.TextOutsideCode);
        Assert.Equal(harmless.CodeSpans.Count, parsed.CodeSpans.Count);
        Assert.Equal(harmless.Fences.Count, parsed.Fences.Count);

        // No HTML, comment, script, image, link, autolink, link definition, table or stray code block.
        Assert.Empty(parsed.ActiveContent);

        // Nothing of the hostile text is outside code formatting, and it did get in.
        Assert.DoesNotContain(Marker, parsed.TextOutsideCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Forged", parsed.TextOutsideCode, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker", parsed.TextOutsideCode, StringComparison.Ordinal);
        Assert.True(parsed.CodeSpans.Count(span => span.Contains(Marker, StringComparison.Ordinal)) >= 12);
        Assert.Contains(parsed.Fences, fence => fence.Contains("# Forged heading", StringComparison.Ordinal) && fence.Contains("<script>alert(1)</script>", StringComparison.Ordinal));

        // No forged heading, review line, evidence entry or citation.
        Assert.Equal(11 + 2, parsed.Headings.Count);
        Assert.All(parsed.Headings.Take(11).Zip(FixedHeadings), pair => Assert.Equal(pair.Second, pair.First));
        Assert.StartsWith("E1: ", parsed.Headings[11], StringComparison.Ordinal);
        Assert.StartsWith("E2: ", parsed.Headings[12], StringComparison.Ordinal);

        // The same without a parser, on the raw lines outside the two fenced blocks: one
        // review line, two evidence headings, one step, and not one line more than the
        // harmless document has, so no single-line field grew a second line.
        var outside = MarkdownOracle.OutsideFences(markdown);
        Assert.Equal(1, outside.Count(line => line.StartsWith("- Reviewed:", StringComparison.Ordinal)));
        Assert.Contains("- Reviewed: no", outside);
        Assert.Equal(2, outside.Count(line => line.StartsWith("### E", StringComparison.Ordinal)));
        Assert.Equal(1, outside.Count(line => line.StartsWith("1. ", StringComparison.Ordinal)));
        Assert.Equal(MarkdownOracle.OutsideFences(PlanMarkdown.Render(Shaped(text => "harmless", path => "docs/harmless.md"))).Count, outside.Count);
        Assert.Equal(LinesOutsideFences(harmless), LinesOutsideFences(parsed));

        // Nothing that breaks a line in some readers, hides text or reorders it survives anywhere.
        Assert.DoesNotContain(markdown, c => c is '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029' or '\0' or '\u001B');
        Assert.DoesNotContain(markdown, c => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\u061C' or '\uFEFF' or '\u200B');
        Assert.DoesNotContain(markdown, c => char.IsSurrogate(c));
    }

    [Fact]
    public void File_name_is_made_of_fixed_words_the_issue_number_and_hex_digits_only()
    {
        var name = PlanMarkdown.FileName(PlanId, 3);

        Assert.Equal("trace-plan-issue-3-01938a000000700080000000000000a1.md", name);
        Assert.Matches(@"\Atrace-plan-issue-[0-9]+-[0-9a-f]{32}\.md\z", name);
        Assert.Matches(@"\Atrace-plan-issue-[0-9]+-[0-9a-f]{32}\.md\z", PlanMarkdown.FileName(Guid.NewGuid(), int.MaxValue));
        Assert.Equal("text/markdown; charset=utf-8", PlanMarkdown.ContentType);
    }

    private static int LinesOutsideFences(MarkdownOracle parsed) =>
        parsed.Document.LineCount - parsed.Fences.Sum(fence => fence.Split('\n').Length);

    private static PlanExport Sample() =>
        new(
            PlanId,
            InvestigationId,
            "trace-demo",
            "sample-repo",
            IssueNumber: 3,
            "First page of the task list skips the oldest tasks",
            "0123456789abcdef0123456789abcdef01234567",
            "claude-test-model",
            "plan-prompt-v1",
            RequestedAt: new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
            ReviewedAt: new DateTimeOffset(2026, 11, 23, 12, 30, 45, TimeSpan.Zero),
            SampleResult(),
            new PlanTrimmed(Citations: 4, Steps: 1, Files: 2, Risks: 0, Verification: 0, Unknowns: 3, SummaryReplaced: false));

    private static PlanResult SampleResult()
    {
        var result = new PlanResult(
            PlanResult.CurrentSchemaVersion,
            PlanOutcome.Planned,
            new PlanStatement("The list skips a full page on page 1; skip (Page - 1) pages instead.", [Bug]),
            Steps:
            [
                new PlanStatement("Add a failing test for the first page.", [Test]),
                new PlanStatement("Skip (Page - 1) * PageSize items in List.", [Bug, Limit]),
            ],
            FilesToChange:
            [
                new PlanFile("src/TaskBoard.Api/Tasks/TaskRepository.cs", PlanFileChange.Modify, "The skip is computed here.", [Bug]),
                new PlanFile("tests/TaskBoard.Tests/TaskListPagingTests.cs", PlanFileChange.Create, "A file for the paging tests.", []),
                new PlanFile("src/TaskBoard.Api/Tasks/PageRequest.cs", PlanFileChange.Delete, "Folded into the repository.", [Limit]),
            ],
            Risks: [new PlanStatement("Clients that ask for page 0 will change behaviour.", [Bug])],
            Verification: [new PlanStatement("Run the repository tests.", [Test])],
            Unknowns: [new PlanStatement("Whether any client asks for page 0.", [])],
            Evidence:
            [
                new PlanEvidence(
                    Bug, "src/TaskBoard.Api/Tasks/TaskRepository.cs", 24, 26,
                    "        var skipped = request.Page * request.PageSize;\n        return tasks.Skip(skipped)\n            .Take(request.PageSize);"),
                new PlanEvidence(Test, "tests/TaskBoard.Tests/TaskRepositoryTests.cs", 8, 9, "    [Fact]\n    public void First_page()"),
                new PlanEvidence(Limit, "src/TaskBoard.Api/Tasks/PageRequest.cs", 5, 5, "public sealed record PageRequest(int Page, int PageSize);"),
            ]);
        result.EnsureValid();
        return result;
    }

    /// <summary>A valid plan with one entry of every kind, whose every text and path comes from the caller.</summary>
    private static PlanExport Shaped(Func<string, string> text, Func<string, string> path)
    {
        var result = new PlanResult(
            PlanResult.CurrentSchemaVersion,
            PlanOutcome.Planned,
            new PlanStatement(text("summary"), [Bug]),
            Steps: [new PlanStatement(text("step"), [Bug, Test])],
            FilesToChange:
            [
                new PlanFile(path("modified"), PlanFileChange.Modify, text("reason"), [Bug]),
                new PlanFile(path("created") + ".new", PlanFileChange.Create, text("new file reason"), []),
            ],
            Risks: [new PlanStatement(text("risk"), [Test])],
            Verification: [new PlanStatement(text("verification"), [Test])],
            Unknowns: [new PlanStatement(text("unknown"), [])],
            Evidence:
            [
                new PlanEvidence(Bug, path("evidence"), 1, 2, text("excerpt")),
                new PlanEvidence(Test, path("other") + ".cs", 7, 7, text("second excerpt")),
            ]);

        // What a stored plan may hold is exactly what must be safe to export. NUL is the one
        // character storage refuses, so the hostile text reaches the exporter directly.
        Assert.All(result.Validate(), problem => Assert.Contains("NUL", problem, StringComparison.Ordinal));

        return new PlanExport(
            PlanId,
            InvestigationId,
            text("owner"),
            text("name"),
            IssueNumber: 3,
            text("title"),
            text("commit"),
            text("model"),
            text("prompt"),
            RequestedAt: new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
            ReviewedAt: null,
            result,
            PlanTrimmed.Nothing);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Trace.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate Trace.slnx.");
    }
}
