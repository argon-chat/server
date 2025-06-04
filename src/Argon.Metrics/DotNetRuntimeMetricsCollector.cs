namespace Argon.Metrics;

public class DotNetRuntimeMetricsCollector(IMetricsCollector metrics, ILogger<DotNetRuntimeMetricsCollector> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the background service loop that periodically collects and pushes .NET runtime metrics until cancellation is requested.
    /// </summary>
    /// <param name="stoppingToken">Token used to signal service cancellation.</param>
    /// <returns>A task representing the asynchronous execution of the service.</returns>
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

    /// <summary>
    /// Collects current .NET runtime metrics and asynchronously pushes them to the metrics collector.
    /// </summary>
    /// <remarks>
    /// Metrics gathered include total managed memory usage, garbage collection counts for generations 0–2, and thread pool statistics (available, minimum, and maximum worker and IO threads).
    /// </remarks>
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
        await metrics.GaugeAsync(MeasurementId.Dotnet.ThreadPool.IOMin, minIO);
        await metrics.GaugeAsync(MeasurementId.Dotnet.ThreadPool.IOMax, maxIO);
    }
}