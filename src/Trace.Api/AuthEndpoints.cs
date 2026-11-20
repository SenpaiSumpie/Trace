using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Trace.Application.Access;
using Trace.Contracts.V1;
using Trace.Infrastructure.GitHub;

namespace Trace.Api;

/// <summary>The signed-in user of the current HTTP request, from the session cookie.</summary>
internal sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string UserIdClaim = "trace_user_id";

    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(UserIdClaim), out var userId) ? userId : null;
}

/// <summary>
/// Sign in with GitHub, sign out, "who am I", and the GitHub webhook receiver.
/// <para>
/// The session is an encrypted, HttpOnly, SameSite=Lax cookie holding only the user's TRACE ID
/// and login. No GitHub token is kept after sign-in.
/// </para>
/// </summary>
internal static class AuthEndpoints
{
    public const string AllowAnonymousWritesKey = "Auth:AllowAnonymousWrites";

    private const string StateCookie = "trace_oauth_state";
    private const int MaxWebhookBytes = 5 * 1024 * 1024;

    public static void AddTraceAuthentication(this WebApplicationBuilder builder)
    {
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
            catch (GitHubApiException)
            {
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

    private static CookieSecurePolicy SecurePolicy(IHostEnvironment environment) =>
        environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
}
