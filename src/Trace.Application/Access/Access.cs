using System.Text.RegularExpressions;

namespace Trace.Application.Access;

/// <summary>
/// Who is making the current request. Null means anonymous, which can only ever see the public
/// demo repository. Background workers do not use this: they act on work that was already
/// authorized when it was queued.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    /// <summary>
    /// For an anonymous caller whose browser carries a visitor cookie: the stored form of that
    /// cookie (<c>Investigation.VisitorKey</c>). It identifies a browser, not a person, and
    /// grants nothing except listing what that browser asked. Null for a signed-in user, for a
    /// caller without the cookie, and outside a request.
    /// </summary>
    string? VisitorKey => null;
}

public sealed class AnonymousUser : ICurrentUser
{
    public Guid? UserId => null;
}

/// <summary>
/// Looks for credentials in source text before it is indexed. A file with any match is not
/// indexed at all: once indexed, a secret would be stored, embedded, and sent to model providers.
/// Deliberately errs towards skipping files; it is a safety net, not a guarantee.
/// </summary>
public static partial class SecretScanner
{
    public static bool ContainsLikelySecret(string text) => Patterns().IsMatch(text);

    [GeneratedRegex(
        """
        -----BEGIN\ [A-Z ]*PRIVATE\ KEY-----          # PEM private keys
        | \bAKIA[0-9A-Z]{16}\b                        # AWS access key ID
        | \bgh[pousr]_[A-Za-z0-9]{36,}                # GitHub tokens
        | \bgithub_pat_[A-Za-z0-9_]{40,}              # GitHub fine-grained tokens
        | \bxox[abprs]-[A-Za-z0-9-]{10,}              # Slack tokens
        | \bsk-ant-[A-Za-z0-9_-]{20,}                 # Anthropic keys
        | \bsk-(?:proj-)?[A-Za-z0-9_-]{32,}           # OpenAI-style keys
        | \b[sr]k_live_[A-Za-z0-9]{20,}               # Stripe live keys
        | \bAIza[0-9A-Za-z_-]{35}\b                   # Google API keys
        | \beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}   # JSON Web Tokens
        | (?i:password|passwd|secret|api[_-]?key|access[_-]?key|auth[_-]?token|client[_-]?secret)
          ["']?\s*[:=]\s*["'][^"'\s$<{]{16,}["']     # long literal assigned to a secret-looking name
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 2_000)]
    private static partial Regex Patterns();
}
