namespace TaskBoard.Api.RateLimiting;

/// <summary>Limits how many requests one API key may make in each one-minute window.</summary>
public sealed class RequestThrottle(RequestDelegate next, IConfiguration configuration, TimeProvider clock)
{
    private readonly int limit = configuration.GetValue("RateLimit:RequestsPerMinute", 60);
    private readonly object gate = new();
    private readonly Dictionary<string, Window> windows = [];

    public async Task InvokeAsync(HttpContext context)
    {
        var key = context.Request.Headers["X-Api-Key"].ToString();
        var now = clock.GetUtcNow();
        TimeSpan? retryAfter = null;

        lock (gate)
        {
            if (!windows.TryGetValue(key, out var window) || now - window.StartedAt >= TimeSpan.FromMinutes(1))
            {
                window = new Window(now, 0);
            }

            if (window.Count >= limit)
            {
                retryAfter = window.StartedAt + TimeSpan.FromMinutes(1) - now;
            }
            else
            {
                windows[key] = window with { Count = window.Count + 1 };
            }
        }

        if (retryAfter is { } wait)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString("0");
            await context.Response.WriteAsJsonAsync(new { error = "Too many requests. Try again later." });
            return;
        }

        await next(context);
    }

    private readonly record struct Window(DateTimeOffset StartedAt, int Count);
}
