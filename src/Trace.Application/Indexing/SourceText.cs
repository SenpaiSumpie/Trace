using System.Text;

namespace Trace.Application.Indexing;

public enum SkipReason
{
    TooLarge,
    Binary,
    InvalidEncoding,
    LineTooLong,
    Empty,
}

public static class SourceText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes file bytes as strict UTF-8 and normalizes line endings to <c>\n</c>, so the same
    /// content indexes identically on every platform. Returns false with a reason for content
    /// that must not be indexed.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out string text, out SkipReason skipReason)
    {
        text = string.Empty;
        skipReason = default;

        if (bytes.Length > SourceFilePolicy.MaxFileBytes)
        {
            skipReason = SkipReason.TooLarge;
            return false;
        }

        if (bytes.Contains((byte)0))
        {
            skipReason = SkipReason.Binary;
            return false;
        }

        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            bytes = bytes[Encoding.UTF8.Preamble.Length..];
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            skipReason = SkipReason.InvalidEncoding;
            return false;
        }

        decoded = decoded.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(decoded))
        {
            skipReason = SkipReason.Empty;
            return false;
        }

        foreach (var line in decoded.AsSpan().EnumerateLines())
        {
            if (line.Length > SourceFilePolicy.MaxLineLength)
            {
                skipReason = SkipReason.LineTooLong;
                return false;
            }
        }

        text = decoded;
        return true;
    }
}
