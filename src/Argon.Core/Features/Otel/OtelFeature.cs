namespace Argon.Features.Otel;

using OpenTelemetry.Metrics;

public class MetricsBasicAuthOptions
{
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public static class OtelFeature
{
    public static void AddOtel(this WebApplicationBuilder builder)
    {
        builder.Services
           .AddOptions<MetricsBasicAuthOptions>()
           .Bind(builder.Configuration.GetSection("Metrics:BasicAuth"))
           .Validate(o => !string.IsNullOrWhiteSpace(o.Username) && !string.IsNullOrWhiteSpace(o.Password),
                "Metrics basic auth is not configured.");
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
            });
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