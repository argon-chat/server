namespace Argon.Metrics;

using Argon.Metrics.Gauges;

public class DotNetRuntimeMetricsCollector(IMetricsCollector metrics, ILogger<DotNetRuntimeMetricsCollector> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(1);

    private readonly DeltaGauge _gen0 = new(metrics, MeasurementId.Dotnet.Gc.Gen0Collections);
    private readonly DeltaGauge _gen1 = new(metrics, MeasurementId.Dotnet.Gc.Gen1Collections);
    private readonly DeltaGauge _gen2 = new(metrics, MeasurementId.Dotnet.Gc.Gen2Collections);

    private readonly EmaGauge _memory = new(metrics, MeasurementId.Dotnet.Gc.TotalMemory, alpha: 0.2);
    private readonly MovingAverageGauge _worker = new(metrics, MeasurementId.Dotnet.ThreadPool.WorkerAvailable, windowSize: 5);
    private readonly MovingAverageGauge _io = new(metrics, MeasurementId.Dotnet.ThreadPool.IOAvailable, windowSize: 5);

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
        var memoryBytes = GC.GetTotalMemory(false);
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);

        ThreadPool.GetAvailableThreads(out var workerAvailable, out var ioAvailable);
        ThreadPool.GetMinThreads(out var workerMin, out var ioMin);
        ThreadPool.GetMaxThreads(out var workerMax, out var ioMax);

        await _memory.ObserveAsync(memoryBytes / 1024.0 / 1024.0);
        await _gen0.ObserveAsync(gen0);
        await _gen1.ObserveAsync(gen1);
        await _gen2.ObserveAsync(gen2);
        await _worker.ObserveAsync(workerAvailable);
        await _io.ObserveAsync(ioAvailable);

        await metrics.ObserveAsync(MeasurementId.Dotnet.ThreadPool.WorkerMin, workerMin);
        await metrics.ObserveAsync(MeasurementId.Dotnet.ThreadPool.WorkerMax, workerMax);
        await metrics.ObserveAsync(MeasurementId.Dotnet.ThreadPool.IOMin, ioMin);
        await metrics.ObserveAsync(MeasurementId.Dotnet.ThreadPool.IOMax, ioMax);
    }
}
