namespace Argon.Features.Systems;

public abstract class RecurringWorkerService<T>(ILogger<T> logger) : BackgroundService where T : RecurringWorkerService<T>
{
    protected async sealed override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await OnCreateAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed execute recurring step");
            }
        }
    }


    protected virtual Task OnCreateAsync(CancellationToken ct = default) => Task.CompletedTask;
    protected abstract Task RunAsync(CancellationToken ct = default);
}