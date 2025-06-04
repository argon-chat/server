namespace Argon.Metrics;

public class DotNetRuntimeMetricsCollector(IMetricsCollector metrics, ILogger<DotNetRuntimeMetricsCollector> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectAndPushAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to collect .NET metrics");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task CollectAndPushAsync()
    {
        var memory = GC.GetTotalMemory(false);
        var gen0   = GC.CollectionCount(0);
        var gen1   = GC.CollectionCount(1);
        var gen2   = GC.CollectionCount(2);

        ThreadPool.GetAvailableThreads(out var worker, out var io);
        ThreadPool.GetMinThreads(out var minWorker, out var minIO);
        ThreadPool.GetMaxThreads(out var maxWorker, out var maxIO);

        await metrics.GaugeAsync(MeasurementId.Dotnet.Gc.TotalMemory, memory / 1024.0 / 1024.0);
        await metrics.GaugeAsync(MeasurementId.Dotnet.Gc.Gen0Collections, gen0);
        await metrics.GaugeAsync(MeasurementId.Dotnet.Gc.Gen1Collections, gen1);
        await metrics.GaugeAsync(MeasurementId.Dotnet.Gc.Gen2Collections, gen2);

        await metrics.GaugeAsync(MeasurementId.Dotnet.ThreadPool.WorkerAvailable, worker);
        await metrics.GaugeAsync(MeasurementId.Dotnet.ThreadPool.IOAvailable, io);
        await metrics.GaugeAsync(MeasurementId.Dotnet.ThreadPool.WorkerMin, minWorker);
        await metrics.GaugeAsync(MeasurementId.Dotnet.ThreadPool.WorkerMax, maxWorker);
    }
}