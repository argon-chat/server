namespace Argon.Features.Otel;

using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

public class MetricsBasicAuthOptions
{
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public static class OtelFeature
{
    private const string HealthEndpointPath    = "/health";
    private const string AlivenessEndpointPath = "/alive";

    public static void AddOtel(this WebApplicationBuilder builder)
    {
        builder.Services
            .AddOptions<MetricsBasicAuthOptions>()
            .Bind(builder.Configuration.GetSection("Metrics:BasicAuth"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Username) && !string.IsNullOrWhiteSpace(o.Password),
                "Metrics basic auth is not configured.");

        static Uri NormalizeOtlpEndpoint(string endpoint, string signalPath)
        {
            var uri = new Uri(endpoint, UriKind.Absolute);

            if (uri.AbsolutePath == "/" || string.IsNullOrWhiteSpace(uri.AbsolutePath))
                return new Uri(uri, signalPath);

            if (uri.AbsolutePath.EndsWith("/v1/metrics", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase))
                return uri;

            var baseUri = uri.AbsoluteUri.TrimEnd('/');
            return new Uri($"{baseUri}{signalPath}");
        }

        var tracesEnv = builder.Configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"];
        var metricsEnv = builder.Configuration["OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"];

        var otel = builder.Services.AddOpenTelemetry();

        if (!string.IsNullOrWhiteSpace(tracesEnv))
        {
            var tracesEndpoint = NormalizeOtlpEndpoint(tracesEnv, "/v1/traces");

            otel.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(t => t.Filter = context =>
                    !context.Request.Path.StartsWithSegments(HealthEndpointPath) &&
                    !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                .AddHttpClientInstrumentation()
                .AddRedisInstrumentation()
                .AddEntityFrameworkCoreInstrumentation()
                .AddOtlpExporter(options => {
                    options.Endpoint = tracesEndpoint;
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                }));
        }

        if (!string.IsNullOrWhiteSpace(metricsEnv))
        {
            var metricsEndpoint = NormalizeOtlpEndpoint(metricsEnv, "/v1/metrics");

            otel.WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Argon")
                .AddMeter("Ion")
                .AddMeter("System.Runtime")
                .AddMeter("Microsoft.AspNetCore")
                .AddMeter("Microsoft.Orleans")
                .AddOtlpExporter(options => {
                    options.Endpoint = metricsEndpoint;
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                }));
        }
    }

    public static void MapOtelExportMetrics(this WebApplication app)
        => app
           .MapPrometheusScrapingEndpoint("/metrics")
           .AddEndpointFilter(async (ctx, next) =>
            {
                var http = ctx.HttpContext;
                var opt  = http.RequestServices.GetRequiredService<IOptionsMonitor<MetricsBasicAuthOptions>>().CurrentValue;

                if (string.IsNullOrWhiteSpace(opt.Username) || string.IsNullOrWhiteSpace(opt.Password))
                    return Results.Problem("Metrics basic auth is not configured.", statusCode: 500);

                if (!http.Request.Headers.TryGetValue("Authorization", out var authHeader))
                    return Results.Unauthorized();

                var auth = authHeader.ToString();
                if (!auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                    return Results.Unauthorized();

                string decoded;
                try
                {
                    var b64 = auth["Basic ".Length..].Trim();
                    decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                }
                catch
                {
                    return Results.Unauthorized();
                }

                var idx = decoded.IndexOf(':');
                if (idx <= 0)
                    return Results.Unauthorized();

                var user = decoded[..idx];
                var pass = decoded[(idx + 1)..];

                if (!string.Equals(user, opt.Username, StringComparison.Ordinal) ||
                    !string.Equals(pass, opt.Password, StringComparison.Ordinal))
                    return Results.Unauthorized();

                return await next(ctx);
            });
}