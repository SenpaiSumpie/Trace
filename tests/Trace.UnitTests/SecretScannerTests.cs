using System.Text;
using Trace.Application.Access;
using Trace.Application.Indexing;

namespace Trace.UnitTests;

/// <remarks>
/// The samples are split with string concatenation on purpose: written as single literals they
/// look enough like real credentials for GitHub's push protection to block the commit.
/// </remarks>
public class SecretScannerTests
{
    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEow...")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    [InlineData("aws_access_key_id = AKIA" + "IOSFODNN7EXAMPLE")]
    [InlineData("token: gh" + "p_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_" + "pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG")]
    [InlineData("SLACK=xo" + "xb-123456789012-abcdefghijkl")]
    [InlineData("key = sk-" + "ant-api03-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("OPENAI_API_KEY=sk-" + "proj-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("stripe.api_key = \"sk_" + "live_abcdefghijklmnopqrstuvwx\"")]
    [InlineData("const google = 'AI" + "zaSyA-abcdefghijklmnopqrstuvwxyz01234';")]
    [InlineData("Authorization: Bearer ey" + "JhbGciOiJIUzI1NiJ9.ey" + "JzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijklmnop")]
    [InlineData("\"password\": \"correct-horse-battery-staple\"")]
    [InlineData("var clientSecret = \"a1b2c3d4e5f6g7h8i9j0\";")]
    [InlineData("API_KEY = 'ZXhhbXBsZS1zZWNyZXQtdmFsdWU'")]
    public void Flags_text_that_looks_like_a_credential(string text)
    {
        Assert.True(SecretScanner.ContainsLikelySecret(text));
    }

    [Theory]
    [InlineData("var headerName = configuration[\"Auth:ApiKeyHeader\"] ?? \"X-Api-Key\";")]
    [InlineData("string? password = null; // read from the vault at runtime")]
    [InlineData("password = Environment.GetEnvironmentVariable(\"DB_PASSWORD\");")]
    [InlineData("\"password\": \"${DB_PASSWORD}\"")]
    [InlineData("public const int MaxPageSize = 100;")]
    [InlineData("var skip = request.Page * request.PageSize;")]
    [InlineData("The secret to good paging is a stable sort order.")]
    [InlineData("apiKey: '<your key here>'")]
    public void Leaves_ordinary_code_alone(string text)
    {
        Assert.False(SecretScanner.ContainsLikelySecret(text));
    }

    [Fact]
    public void File_with_a_credential_is_not_indexable_at_all()
    {
        var bytes = Encoding.UTF8.GetBytes("class Config\n{\n    const string Key = \"AKIA" + "IOSFODNN7EXAMPLE\";\n}\n");

        Assert.False(SourceText.TryDecode(bytes, out var text, out var reason));
        Assert.Equal(SkipReason.PossibleSecret, reason);
        Assert.Empty(text);
    }
}
