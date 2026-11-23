using System.Diagnostics;
using Trace.Application.Telemetry;

namespace Trace.Api;

internal static class CorrelationId
{
    /// <summary>
    /// Gives every request a correlation ID: the caller's <c>X-Correlation-ID</c> if it sent a
    /// well-formed one, otherwise a new one. The ID is returned in the response header, added
    /// to every log line of the request, and carried by any job the request queues, so one
    /// value finds everything that happened because of this request.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CorrelationId).FullName!);

        return app.Use(async (context, next) =>
        {
            var supplied = context.Request.Headers[TraceTelemetry.CorrelationHeader].ToString();

            // Anything else is replaced rather than echoed: the value goes into logs and a header.
            var correlationId = TraceTelemetry.IsValidCorrelationId(supplied) ? supplied : TraceTelemetry.NewCorrelationId();

            TraceTelemetry.SetCorrelationId(Activity.Current, correlationId);
            context.Response.Headers[TraceTelemetry.CorrelationHeader] = correlationId;

            using (logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId }))
            {
                await next(context);
            }
        });
    }
}
