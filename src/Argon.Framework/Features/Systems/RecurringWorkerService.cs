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
                logger.LogError(e, "Failed execute recurring step");
            }
        }
    }

    public sealed override Task StartAsync(CancellationToken cancellationToken)
        => base.StartAsync(cancellationToken);

    public sealed override Task StopAsync(CancellationToken cancellationToken)
        => base.StopAsync(cancellationToken);


    protected virtual Task OnCreateAsync(CancellationToken ct = default) => Task.CompletedTask;
    protected abstract Task RunAsync(CancellationToken ct = default);
}