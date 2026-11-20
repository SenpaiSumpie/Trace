using System.Text;
using Trace.Application.Indexing;

namespace Trace.UnitTests;

public class SourcePolicyTests
{
    [Theory]
    [InlineData("Program.cs", "csharp")]
    [InlineData("api.ts", "typescript")]
    [InlineData("TaskList.tsx", "typescript")]
    [InlineData("index.JS", "javascript")]
    [InlineData("README.md", "markdown")]
    [InlineData("appsettings.json", "json")]
    [InlineData("ci.yml", "yaml")]
    public void Allowlisted_extensions_map_to_a_language(string fileName, string language)
    {
        Assert.Equal(language, SourceFilePolicy.LanguageFor(fileName));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.json")]
    [InlineData("id_rsa")]
    [InlineData("server.pem")]
    [InlineData("secrets.key")]
    [InlineData("logo.png")]
    [InlineData("app.dll")]
    [InlineData("package-lock.json")]
    [InlineData("bundle.min.js")]
    [InlineData("Model.g.cs")]
    [InlineData("types.d.ts")]
    [InlineData("Makefile")]
    public void Everything_else_is_not_indexable(string fileName)
    {
        Assert.Null(SourceFilePolicy.LanguageFor(fileName));
    }

    [Theory]
    [InlineData(".git", true)]
    [InlineData(".github", true)]
    [InlineData("node_modules", true)]
    [InlineData("BIN", true)]
    [InlineData("obj", true)]
    [InlineData("src", false)]
    [InlineData("docs", false)]
    public void Dot_and_build_directories_are_excluded(string name, bool excluded)
    {
        Assert.Equal(excluded, SourceFilePolicy.IsExcludedDirectory(name));
    }

    [Fact]
    public void Decoding_normalizes_line_endings_and_strips_the_byte_order_mark()
    {
        byte[] bytes = [.. Encoding.UTF8.Preamble, .. "one\r\ntwo\rthree\n"u8];

        Assert.True(SourceText.TryDecode(bytes, out var text, out _));
        Assert.Equal("one\ntwo\nthree\n", text);
    }

    [Fact]
    public void Decoding_preserves_non_ascii_text()
    {
        Assert.True(SourceText.TryDecode("// héllo → 世界\n"u8, out var text, out _));
        Assert.Equal("// héllo → 世界\n", text);
    }

    [Fact]
    public void Decoding_rejects_binary_content()
    {
        Assert.False(SourceText.TryDecode([0x4D, 0x5A, 0x00, 0x01], out _, out var reason));
        Assert.Equal(SkipReason.Binary, reason);
    }

    [Fact]
    public void Decoding_rejects_invalid_utf8()
    {
        Assert.False(SourceText.TryDecode([0x61, 0xFF, 0xFE, 0x62], out _, out var reason));
        Assert.Equal(SkipReason.InvalidEncoding, reason);
    }

    [Fact]
    public void Decoding_rejects_oversized_files()
    {
        var bytes = Enumerable.Repeat((byte)'a', SourceFilePolicy.MaxFileBytes + 1).ToArray();

        Assert.False(SourceText.TryDecode(bytes, out _, out var reason));
        Assert.Equal(SkipReason.TooLarge, reason);
    }

    [Fact]
    public void Decoding_rejects_minified_content()
    {
        var bytes = Encoding.UTF8.GetBytes("ok\n" + new string('x', SourceFilePolicy.MaxLineLength + 1));

        Assert.False(SourceText.TryDecode(bytes, out _, out var reason));
        Assert.Equal(SkipReason.LineTooLong, reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \n\t\n")]
    public void Decoding_rejects_empty_content(string content)
    {
        Assert.False(SourceText.TryDecode(Encoding.UTF8.GetBytes(content), out _, out var reason));
        Assert.Equal(SkipReason.Empty, reason);
    }
}
