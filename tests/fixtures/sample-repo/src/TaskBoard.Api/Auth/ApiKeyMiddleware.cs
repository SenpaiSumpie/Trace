namespace TaskBoard.Api.Auth;

/// <summary>Rejects requests that do not carry the configured API key.</summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private readonly string headerName = configuration["Auth:ApiKeyHeader"] ?? "X-Api-Key";
    private readonly string? expectedKey = configuration["Auth:ApiKey"];

    public async Task InvokeAsync(HttpContext context)
    {
        // The health probe is called by the load balancer, which cannot send a key.
        if (context.Request.Path.Value!.Contains("/health"))
        {
            await next(context);
            return;
        }

        var presented = context.Request.Headers[headerName].ToString();
        if (string.IsNullOrEmpty(expectedKey) || presented != expectedKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A valid API key is required." });
            return;
        }

        await next(context);
    }
}
