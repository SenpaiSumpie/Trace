using Trace.Application.Issues;

namespace Trace.UnitTests;

/// <summary>
/// The fingerprint a plan keeps of the issue it was made from. Its exact definition is pinned
/// here so the step that computes it for real plans and the step that compares it later get
/// the same value.
/// </summary>
public class IssueContentHashTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_hash_is_the_sha256_of_a_fixed_length_prefixed_layout()
    {
        var issue = Issue();

        Assert.Equal(
            "trace-issue-v1\n42\n17:Cap the page size\n17:Line one\nLine two\n2\n9001\n5:First\n9002\n0:\n",
            IssueContentHash.Canonical(issue));

        // Computed independently: sha256 of the UTF-8 bytes of the text above.
        Assert.Equal("c048132f156e9eb1da251d04ce040c1c4a2a9093f1876668299438cdba6f06a1", IssueContentHash.Compute(issue));
    }

    [Fact]
    public void Lengths_count_utf16_characters_and_the_bytes_hashed_are_utf8()
    {
        var issue = Issue() with
        {
            Summary = Summary() with { Number = 7, Title = "é\U0001F600" },
            Body = string.Empty,
            Comments = [],
        };

        Assert.Equal("trace-issue-v1\n7\n3:é\U0001F600\n0:\n0\n", IssueContentHash.Canonical(issue));
        Assert.Equal("b915b95b6017742d06a120b9638f2f36ad2b0ab9d8bb895177dcea3c74f4b0d4", IssueContentHash.Compute(issue));
    }

    [Fact]
    public void Any_change_to_the_text_a_plan_is_made_from_changes_the_hash()
    {
        var issue = Issue();
        IssueDetail[] changed =
        [
            issue with { Summary = Summary() with { Title = "Cap the page size!" } },
            issue with { Summary = Summary() with { Number = 43 } },
            issue with { Body = "Line one\nLine 2" },
            issue with { Comments = [issue.Comments[0] with { Body = "First, edited" }, issue.Comments[1]] },
            issue with { Comments = [issue.Comments[1], issue.Comments[0]] },
            issue with { Comments = [issue.Comments[0]] },
            issue with { Comments = [.. issue.Comments, new IssueComment(9003, "carol", "Third", false, Day)] },
            issue with { Comments = [issue.Comments[0] with { Id = 9100 }, issue.Comments[1]] },
        ];

        var hashes = changed.Select(IssueContentHash.Compute).Append(IssueContentHash.Compute(issue)).ToList();

        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    [Fact]
    public void State_labels_authors_dates_and_counts_are_not_part_of_the_hash()
    {
        var issue = Issue();
        IssueDetail[] sameText =
        [
            issue with { Summary = Summary() with { State = "closed" } },
            issue with { Summary = Summary() with { Labels = ["bug", "wontfix"] } },
            issue with { Summary = Summary() with { Author = null } },
            issue with { Summary = Summary() with { CommentCount = 99, UpdatedAt = Day.AddDays(30), CreatedAt = Day.AddDays(-1) } },
            issue with { BodyTruncated = true, CommentsTruncated = true },
            issue with
            {
                Comments = [issue.Comments[0] with { Author = "mallory", CreatedAt = Day.AddDays(2), BodyTruncated = true }, issue.Comments[1]],
            },
        ];

        Assert.All(sameText, same => Assert.Equal(IssueContentHash.Compute(issue), IssueContentHash.Compute(same)));
    }

    [Fact]
    public void Text_cannot_be_moved_between_fields_to_keep_the_same_hash()
    {
        // Without lengths, a title ending where the body begins could trade lines with it.
        var first = Issue() with { Summary = Summary() with { Title = "a" }, Body = "b\n1:c", Comments = [] };
        var second = Issue() with { Summary = Summary() with { Title = "a\n1:b" }, Body = "c", Comments = [] };
        var commentAsBody = Issue() with { Body = "Line one\nLine two\n1\n9001\n5:First", Comments = [] };

        Assert.NotEqual(IssueContentHash.Compute(first), IssueContentHash.Compute(second));
        Assert.NotEqual(IssueContentHash.Compute(Issue()), IssueContentHash.Compute(commentAsBody));
    }

    [Fact]
    public void The_hash_is_taken_of_the_issue_as_the_reader_bounds_it()
    {
        var thread = new RawIssueThread(
            new RawIssue(42, "Cap the\0 page size\n", "open", "alice", ["bug"], "Line one\nLine two", 2, Day, Day),
            [new RawIssueComment(9001, "bob", "First", Day), new RawIssueComment(9002, "bob", null, Day)]);
        var longer = thread with
        {
            Issue = thread.Issue with { Body = new string('x', IssueLimits.MaxBodyLength) + "cut off" },
        };
        var longerOtherwise = thread with
        {
            Issue = thread.Issue with { Body = new string('x', IssueLimits.MaxBodyLength) + "something else" },
        };

        var hash = IssueContentHash.Compute(IssueReader.Bound(thread));

        Assert.Equal(IssueContentHash.Compute(Issue()), hash);
        Assert.Matches("^[0-9a-f]{64}$", hash);

        // A known limit: what the size limits cut off is not seen, so a change there is not detected.
        Assert.Equal(
            IssueContentHash.Compute(IssueReader.Bound(longer)),
            IssueContentHash.Compute(IssueReader.Bound(longerOtherwise)));
    }

    private static IssueSummary Summary() =>
        new(42, "Cap the page size", "open", "alice", ["bug"], 2, Day, Day);

    private static IssueDetail Issue() => new(
        Summary(),
        "Line one\nLine two",
        BodyTruncated: false,
        [new IssueComment(9001, "bob", "First", false, Day), new IssueComment(9002, "bob", string.Empty, false, Day)],
        CommentsTruncated: false);
}
