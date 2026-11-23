using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Trace.Application.Telemetry;

namespace Trace.Infrastructure.Telemetry;

public static class TelemetryServiceCollectionExtensions
{
    /// <summary>Standard OpenTelemetry setting: where to send traces and metrics (OTLP).</summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Records traces and metrics for TRACE's own work and its outgoing HTTP calls.
    /// <para>
    /// Spans are always created, so every log line carries a trace ID and jobs continue the
    /// trace of the request that queued them. They are sent somewhere only when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set; without it nothing leaves the process.
    /// </para>
    /// </summary>
    /// <param name="serviceName">How this process is named in telemetry, e.g. <c>trace-api</c>.</param>
    /// <param name="configureTracing">Host-specific instrumentation, such as incoming HTTP requests.</param>
    /// <param name="configureMetrics">Host-specific metrics.</param>
    public static IServiceCollection AddTraceTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                tracing.AddSource(TraceTelemetry.SourceName);

                // Outgoing calls to GitHub and the model providers. Request and response bodies
                // and headers are not recorded, and query string values are redacted.
                tracing.AddHttpClientInstrumentation();
                configureTracing?.Invoke(tracing);
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(TraceTelemetry.SourceName);
                metrics.AddHttpClientInstrumentation();
                configureMetrics?.Invoke(metrics);
            });

        if (configuration[OtlpEndpointKey] is { Length: > 0 })
        {
            telemetry.UseOtlpExporter();
        }

        return services;
    }
}
