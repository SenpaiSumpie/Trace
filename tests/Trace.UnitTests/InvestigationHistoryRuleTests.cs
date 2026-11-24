using System.Buffers.Text;
using System.Text;
using Trace.Application.Investigations;
using Trace.Domain;

namespace Trace.UnitTests;

public class InvestigationHistoryRuleTests
{
    private const string VisitorKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Cursor_round_trips_and_does_not_show_its_contents()
    {
        var cursor = new InvestigationCursor(
            new DateTimeOffset(2026, 11, 23, 10, 30, 15, 123, TimeSpan.Zero).AddTicks(4560), Guid.CreateVersion7());

        var text = cursor.Encode();

        Assert.Equal(cursor, InvestigationCursor.Parse(text));
        Assert.Matches("^[A-Za-z0-9_-]+$", text);
        Assert.DoesNotContain(cursor.Id.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
        Assert.True(text.Length <= InvestigationCursor.MaxLength);
    }

    [Fact]
    public void Cursor_written_with_another_offset_is_the_same_position_in_utc()
    {
        var id = Guid.CreateVersion7();
        var utc = new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            new InvestigationCursor(utc, id).Encode(),
            new InvestigationCursor(utc.ToOffset(TimeSpan.FromHours(5)), id).Encode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a cursor")]
    [InlineData("AAAA")]
    [InlineData("1")]
    [InlineData("../../etc/passwd")]
    [InlineData("%00")]
    public void Text_that_is_not_a_cursor_is_refused(string? text) => Assert.Null(InvestigationCursor.Parse(text));

    [Theory]
    [InlineData("h1:1:00000000000000000000000000000000:extra")]
    [InlineData("h2:1:00000000000000000000000000000000")]
    [InlineData("h1:-1:00000000000000000000000000000000")]
    [InlineData("h1: 1:00000000000000000000000000000000")]
    [InlineData("h1:01:00000000000000000000000000000000")]
    [InlineData("h1:9223372036854775807:00000000000000000000000000000000")]
    [InlineData("h1:99999999999999999999999999:00000000000000000000000000000000")]
    [InlineData("h1:1:00000000-0000-0000-0000-000000000000")]
    [InlineData("h1:1:0000000000000000000000000000000G")]
    [InlineData("h1:1:0000000000000000000000000000000A")]
    [InlineData("h1:1")]
    [InlineData("issue:1:2")]
    public void Well_encoded_text_with_the_wrong_contents_is_refused(string contents) =>
        Assert.Null(InvestigationCursor.Parse(Base64Url.EncodeToString(Encoding.ASCII.GetBytes(contents))));

    [Fact]
    public void Cursor_longer_than_the_limit_is_refused_without_being_decoded() =>
        Assert.Null(InvestigationCursor.Parse(new string('A', InvestigationCursor.MaxLength + 1)));

    [Fact]
    public void Anonymous_investigation_keeps_the_visitor_key_and_a_signed_in_one_has_none()
    {
        var snapshot = CompletedSnapshot();
        var userId = Guid.CreateVersion7();

        var anonymous = Investigation.Create(snapshot, InvestigationType.Question, "q?", visitorKey: VisitorKey);
        var signedIn = Investigation.Create(snapshot, InvestigationType.Question, "q?", userId: userId);

        Assert.Equal((null, VisitorKey), (anonymous.UserId, anonymous.VisitorKey));
        Assert.Equal((userId, null), (signedIn.UserId, signedIn.VisitorKey));
    }

    [Fact]
    public void Investigation_cannot_belong_to_a_user_and_a_visitor_at_once() =>
        Assert.Throws<ArgumentException>(() => Investigation.Create(
            CompletedSnapshot(), InvestigationType.Question, "q?", userId: Guid.CreateVersion7(), visitorKey: VisitorKey));

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void Visitor_key_must_be_a_lowercase_hex_sha256(string key)
    {
        Assert.False(Investigation.IsVisitorKey(key));
        Assert.Throws<ArgumentException>(() => Investigation.Create(
            CompletedSnapshot(), InvestigationType.Question, "q?", visitorKey: key));
    }

    private static IndexSnapshot CompletedSnapshot()
    {
        var snapshot = IndexSnapshot.Start(
            Guid.CreateVersion7(), new string('a', 40), "main", parserVersion: "text-v1", embeddingModel: "fake-v1");
        snapshot.Complete(DateTimeOffset.UtcNow);
        return snapshot;
    }
}
