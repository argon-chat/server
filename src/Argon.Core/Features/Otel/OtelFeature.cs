namespace Argon.Features.Otel;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

public static class OtelFeature
{
    private const string HealthEndpointPath    = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static void AddOtel(this WebApplicationBuilder builder)
    {
        builder.Services.AddOpenTelemetry()
           .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                   .AddHttpClientInstrumentation()
                   .AddRuntimeInstrumentation()
                   .AddMeter("Argon")
                   .AddMeter("Ion")
                   .AddMeter("System.Runtime")
                   .AddMeter("Microsoft.AspNetCore")
                   .AddMeter("Microsoft.Orleans")
                   .AddPrometheusExporter();
            })
           .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                   .AddAspNetCoreInstrumentation(t =>
                        t.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                   .AddHttpClientInstrumentation()
                   .AddRedisInstrumentation()
                   .AddEntityFrameworkCoreInstrumentation();
            });

        builder.AddOpenTelemetryExporters();
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
            builder.Services.AddOpenTelemetry().UseOtlpExporter();

        return builder;
    }
}