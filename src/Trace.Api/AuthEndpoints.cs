using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Trace.Application.Access;
using Trace.Contracts.V1;
using Trace.Infrastructure.GitHub;
using Trace.Infrastructure.Persistence;

namespace Trace.Api;

/// <summary>The signed-in user of the current HTTP request, from the session cookie.</summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string UserIdClaim = "trace_user_id";

    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(UserIdClaim), out var userId) ? userId : null;

    /// <summary>The stored form of this browser's visitor cookie; null for a signed-in user or without the cookie.</summary>
    public string? VisitorKey =>
        UserId is null && accessor.HttpContext is { } context ? VisitorCookie.KeyOf(context) : null;
}

/// <summary>
/// Lets an anonymous browser find its own past investigations again (ADR-020). The cookie
/// holds 32 random bytes and nothing else; an investigation stores only the SHA-256 of them, so
/// reading the database does not yield a cookie. It identifies a browser, not a person: it is
/// never read for a signed-in user, grants no access to anything (reading by ID and repository
/// visibility do not look at it) and only narrows the history list to what this browser asked.
/// It is issued only when an anonymous caller is allowed to start an investigation and does.
/// </summary>
internal static class VisitorCookie
{
    public const string Name = "trace_visitor";

    private const int TokenLength = 64;
    private static readonly object IssuedKey = new();

    /// <summary>The key investigations of this browser are stored under, or null when it has no valid cookie.</summary>
    public static string? KeyOf(HttpContext context)
    {
        var token = context.Items[IssuedKey] as string ?? context.Request.Cookies[Name];
        return IsToken(token)
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token!)))
            : null;
    }

    /// <summary>Gives an anonymous browser its cookie if it has none, before the investigation is created.</summary>
    public static void EnsureIssued(HttpContext context, IHostEnvironment environment)
    {
        if (context.User.Identity is { IsAuthenticated: true } || IsToken(context.Request.Cookies[Name]))
        {
            return;
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(TokenLength / 2));
        context.Items[IssuedKey] = token;
        context.Response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = !environment.IsDevelopment() || context.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(180),
            Path = "/api/v1",
            IsEssential = true,
        });
    }

    private static bool IsToken(string? value) =>
        value is { Length: TokenLength } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

/// <summary>
/// Sign in with GitHub, sign out, "who am I", and the GitHub webhook receiver.
/// <para>
/// The session is an encrypted, HttpOnly, SameSite=Lax cookie holding only the user's TRACE ID
/// and login. No GitHub token is kept after sign-in.
/// </para>
/// </summary>
internal static partial class AuthEndpoints
{
    public const string AllowAnonymousWritesKey = "Auth:AllowAnonymousWrites";

    private const string StateCookie = "trace_oauth_state";
    private const int MaxWebhookBytes = 5 * 1024 * 1024;

    public static void AddTraceAuthentication(this WebApplicationBuilder builder)
    {
        builder.Services.AddDataProtection()
            .SetApplicationName("trace")
            .PersistKeysToDbContext<TraceDbContext>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "trace_session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = SecurePolicy(builder.Environment);

                // Access is re-read from GitHub at sign-in, so the session length bounds how long
                // access that was revoked on GitHub can linger here.
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = false;

                // An API: answer 401 rather than redirecting to a login page.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            });
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/me", (HttpContext context, GitHubOptions gitHub, IConfiguration configuration) =>
        {
            var login = context.User.Identity is { IsAuthenticated: true } ? context.User.Identity.Name : null;
            return TypedResults.Ok(new MeResponse(
                login is null ? null : new UserResponse(login),
                gitHub.SignInConfigured,
                login is not null || configuration.GetValue(AllowAnonymousWritesKey, false)));
        });

        v1.MapGet("/auth/login", (HttpContext context, GitHubOptions gitHub, IWebHostEnvironment environment) =>
        {
            if (!gitHub.SignInConfigured)
            {
                return Results.Problem(detail: "Sign-in is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Ties the callback to this browser: a login response cannot be injected into another session.
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(StateCookie, state, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = SecurePolicy(environment) == CookieSecurePolicy.Always || context.Request.IsHttps,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/api/v1/auth",
            });

            var authorize = new Uri(gitHub.WebBaseUrl, "login/oauth/authorize");
            return Results.Redirect(
                $"{authorize}?client_id={Uri.EscapeDataString(gitHub.ClientId!)}&state={state}");
        });

        v1.MapGet("/auth/callback", async (
            string? code,
            string? state,
            HttpContext context,
            GitHubSignIn signIn,
            ILoggerFactory loggers,
            CancellationToken cancellationToken) =>
        {
            var expected = context.Request.Cookies[StateCookie];
            context.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/api/v1/auth" });
            if (string.IsNullOrEmpty(code)
                || string.IsNullOrEmpty(state)
                || string.IsNullOrEmpty(expected)
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(expected)))
            {
                return Results.Problem(detail: "The sign-in response was not valid. Start again.", statusCode: StatusCodes.Status400BadRequest);
            }

            Domain.User user;
            try
            {
                user = await signIn.SignInAsync(code, cancellationToken);
            }
            catch (GitHubApiException exception)
            {
                SignInFailed(loggers.CreateLogger(typeof(AuthEndpoints).FullName!), exception.Message);
                return Results.Problem(detail: "GitHub did not complete the sign-in. Try again.", statusCode: StatusCodes.Status502BadGateway);
            }

            var identity = new ClaimsIdentity(
                [new Claim(HttpContextCurrentUser.UserIdClaim, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Login)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(new ClaimsPrincipal(identity));

            // Always back to the app's own root; never to a caller-supplied address.
            return Results.LocalRedirect("/");
        });

        v1.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync();
            return Results.NoContent();
        });

        v1.MapPost("/github/webhooks", async (HttpContext context, GitHubWebhooks webhooks, CancellationToken cancellationToken) =>
        {
            if (context.Request.ContentLength > MaxWebhookBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > MaxWebhookBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var outcome = await webhooks.HandleAsync(
                context.Request.Headers["X-GitHub-Event"],
                context.Request.Headers["X-GitHub-Delivery"],
                context.Request.Headers["X-Hub-Signature-256"],
                buffer.ToArray(),
                cancellationToken);

            return outcome == WebhookOutcome.Rejected ? Results.Unauthorized() : Results.Accepted();
        });
    }

    /// <summary>
    /// Requests that start or stop work need a signed-in user, unless anonymous use is switched
    /// on (it is in Development, so the demo works without a GitHub App).
    /// </summary>
    public static RouteHandlerBuilder RequireSignIn(this RouteHandlerBuilder endpoint) =>
        endpoint.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var allowed = http.User.Identity is { IsAuthenticated: true }
                || http.RequestServices.GetRequiredService<IConfiguration>().GetValue(AllowAnonymousWritesKey, false);

            return allowed
                ? await next(context)
                : Results.Problem(detail: "Sign in to do this.", statusCode: StatusCodes.Status401Unauthorized);
        });

    /// <summary>
    /// For the requests that create an investigation: an anonymous caller who gets this far
    /// (anonymous use is on) is given the visitor cookie its history is listed by. Put after
    /// <see cref="RequireSignIn"/>, so a refused request sets nothing.
    /// </summary>
    public static RouteHandlerBuilder RememberAnonymousVisitor(this RouteHandlerBuilder endpoint) =>
        endpoint.AddEndpointFilter(async (context, next) =>
        {
            VisitorCookie.EnsureIssued(
                context.HttpContext, context.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>());
            return await next(context);
        });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in failed: {Reason}")]
    private static partial void SignInFailed(ILogger logger, string reason);

    private static CookieSecurePolicy SecurePolicy(IHostEnvironment environment) =>
        environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
}
