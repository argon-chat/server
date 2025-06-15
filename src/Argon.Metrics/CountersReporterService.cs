namespace Argon.Metrics;

public class CountersReporterService(ICounters counters, ILogger<CountersReporterService> logger) : BackgroundService
{
    private readonly PeriodicTimer timer = new(TimeSpan.FromSeconds(10));


    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await counters.ReportAllAsync();
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed sync metric counters");
            }
        }
    }
}