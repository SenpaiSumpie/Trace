using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Trace.Application.Issues;

/// <summary>
/// The fingerprint stored with a plan (<c>plans.issue_content_hash</c>) so that an issue edited
/// after the plan was made can be told from the one the plan read. The issue text itself is
/// not stored (ADR-016).
/// <para>
/// It covers exactly the text a plan may be drawn from, as <see cref="IssueReader"/> returns it
/// (cleaned and cut to <see cref="IssueLimits"/>): the number, the title, the body, and the ID
/// and body of each returned comment, in order. It does not cover the state, labels, authors,
/// dates or the comment count, so closing or relabeling an issue does not make a plan look
/// outdated; and it cannot see what the limits cut off.
/// </para>
/// <para>
/// Whoever shows a model anything of an issue beyond what is covered here must extend the
/// hash, under a new <see cref="Version"/>.
/// </para>
/// </summary>
public static class IssueContentHash
{
    /// <summary>First line of the hashed text. A different way of hashing gets a different name.</summary>
    public const string Version = "trace-issue-v1";

    /// <summary>Lowercase hex SHA-256 of the UTF-8 encoding of <see cref="Canonical"/>.</summary>
    public static string Compute(IssueDetail issue) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(issue))));

    /// <summary>
    /// The text that is hashed. Every piece of issue text is preceded by its length in UTF-16
    /// characters, so no title, body or comment can be written to imitate another layout.
    /// </summary>
    public static string Canonical(IssueDetail issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        var text = new StringBuilder();
        text.Append(Version).Append('\n');
        text.Append(issue.Summary.Number.ToString(CultureInfo.InvariantCulture)).Append('\n');
        Append(text, issue.Summary.Title);
        Append(text, issue.Body);
        text.Append(issue.Comments.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var comment in issue.Comments)
        {
            text.Append(comment.Id.ToString(CultureInfo.InvariantCulture)).Append('\n');
            Append(text, comment.Body);
        }

        return text.ToString();
    }

    private static void Append(StringBuilder text, string value) =>
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
}
