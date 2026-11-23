using System.Text;

namespace Trace.Application.Issues;

/// <summary>
/// How much of an issue TRACE will carry. Issue text is written by whoever can open an issue,
/// so it is bounded before anything else sees it; whatever is cut is flagged, never silently
/// dropped. All lengths are in UTF-16 characters, after cleaning.
/// </summary>
public static class IssueLimits
{
    public const int MaxTitleLength = 256;
    public const int MaxBodyLength = 20_000;
    public const int MaxCommentLength = 4_000;
    public const int MaxComments = 30;

    /// <summary>Body plus all returned comments.</summary>
    public const int MaxTotalLength = 60_000;

    public const int MaxLabels = 20;
    public const int MaxLabelLength = 100;
    public const int MaxAuthorLength = 100;

    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 100;
    public const int MaxCursorLength = 200;
}

/// <summary>An issue as a source delivered it: nothing cleaned or cut yet.</summary>
public sealed record RawIssue(
    int Number,
    string? Title,
    string? State,
    string? Author,
    IReadOnlyList<string?> Labels,
    string? Body,
    int CommentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record RawIssueComment(long Id, string? Author, string? Body, DateTimeOffset CreatedAt);

/// <param name="Comments">Oldest first; a source returns at most the number it was asked for.</param>
public sealed record RawIssueThread(RawIssue Issue, IReadOnlyList<RawIssueComment> Comments);

/// <param name="NextCursor">Opaque; null when there is nothing further.</param>
public sealed record RawIssuePage(IReadOnlyList<RawIssue> Items, string? NextCursor);

public sealed record IssueSummary(
    int Number,
    string Title,
    string State,
    string? Author,
    IReadOnlyList<string> Labels,
    int CommentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record IssuePage(IReadOnlyList<IssueSummary> Items, string? NextCursor);

public sealed record IssueComment(long Id, string? Author, string Body, bool BodyTruncated, DateTimeOffset CreatedAt);

/// <param name="BodyTruncated">The body was longer than <see cref="IssueLimits.MaxBodyLength"/>.</param>
/// <param name="CommentsTruncated">The issue has comments that are not in <paramref name="Comments"/>.</param>
public sealed record IssueDetail(
    IssueSummary Summary,
    string Body,
    bool BodyTruncated,
    IReadOnlyList<IssueComment> Comments,
    bool CommentsTruncated);

/// <summary>
/// Cleans and bounds text that came from an issue. It removes what PostgreSQL text cannot hold
/// (NUL) and what is not valid Unicode (a lone surrogate becomes U+FFFD), and cuts at a limit
/// without splitting a surrogate pair.
/// </summary>
public static class IssueText
{
    /// <summary>Multi-line text: a body or a comment.</summary>
    public static string Clean(string? value, int maxLength, out bool truncated) =>
        Clean(value, maxLength, singleLine: false, out truncated);

    /// <summary>One-line text (title, author, label): control characters become spaces.</summary>
    public static string CleanLine(string? value, int maxLength) =>
        Clean(value, maxLength, singleLine: true, out _).Trim();

    private static string Clean(string? value, int maxLength, bool singleLine, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrEmpty(value) || maxLength <= 0)
        {
            truncated = !string.IsNullOrEmpty(value);
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\0')
            {
                continue;
            }

            var isPair = char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]);
            if (!isPair && char.IsSurrogate(character))
            {
                character = '�';
            }
            else if (singleLine && char.IsControl(character))
            {
                character = ' ';
            }

            if (builder.Length + (isPair ? 2 : 1) > maxLength)
            {
                truncated = true;
                break;
            }

            builder.Append(character);
            if (isPair)
            {
                builder.Append(value[++index]);
            }
        }

        return builder.ToString();
    }
}
