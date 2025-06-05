namespace Argon.Metrics;

using InfluxDB3.Client;
using Microsoft.Extensions.Options;

public static class ArgonMetricsExtensions
{
    public static IServiceCollection AddMetrics(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<InfluxDbOptions>(
            builder.Configuration.GetSection("metrics:influx"));

        builder.Services.AddSingleton(provider =>
        {
            var cfg = provider.GetRequiredService<IOptions<InfluxDbOptions>>().Value;

            return new Lazy<InfluxDBClient>(() => new InfluxDBClient(cfg.Url, cfg.Token));
        });

        builder.Services.AddSingleton<IMetricsCollector, InfluxMetricsCollector>();
        builder.Services.AddSingleton<IPointBuffer, InfluxBatchWriter>();
        builder.Services.AddHostedService(sp => (InfluxBatchWriter)sp.GetRequiredService<IPointBuffer>());
        builder.Services.AddHostedService<DotNetRuntimeMetricsCollector>();

        return builder.Services;
    }

    public static WebApplication UseMetrics(this WebApplication app)
    {
        app.UseMiddleware<MetricsHttpMiddleware>();

        return app;
    }
}