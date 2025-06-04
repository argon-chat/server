namespace Argon.Metrics;

using InfluxDB.Client;
using Microsoft.Extensions.Options;

public static class ArgonMetricsExtensions
{
    /// <summary>
    /// Registers and configures metrics collection services, including InfluxDB integration, for the web application.
    /// </summary>
    /// <returns>The updated service collection with metrics-related services registered.</returns>
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

    /// <summary>
    /// Adds the metrics middleware to the application's request pipeline.
    /// </summary>
    /// <param name="app">The web application instance.</param>
    /// <returns>The web application with metrics middleware configured.</returns>
    public static WebApplication UseMetrics(this WebApplication app)
    {
        app.UseMiddleware<MetricsHttpMiddleware>();

        return app;
    }
}