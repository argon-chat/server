namespace Argon.Features;

using RegionalUnit;
using Systems;
using static Api.Features.Orleans.Client.ArgonDataCenterStatus;

public sealed class DcWatcherService(
    ILogger<DcWatcherService> logger,
    IArgonRegionalBus bus,
    IArgonDcRegistry registry,
    IArgonClusterRouter router,
    IClusterClientFactory clusterClientFactory) : RecurringWorkerService<DcWatcherService>(logger)
{
    private readonly TimeSpan _gcTimeout = TimeSpan.FromMinutes(3);

    protected override Task OnCreateAsync(CancellationToken ct = default)
        => registry.SubscribeToNewClient(OnClusterRegistered);

    private async ValueTask OnClusterRegistered(ArgonDcClusterInfo dc, CancellationToken ct)
        => await dc.serviceProvider
           .GetRequiredService<IClusterClient>()
           .As<IClusterClient, IHostedService>()
           .StartAsync(dc.ctSource.Token);

    protected async override Task RunAsync(CancellationToken ct = default)
    {
        var dcs = await bus.GetAllDcsAsync(ct);
        foreach (var dc in dcs)
        {
            if (!registry.TryGet(dc, out var existing))
            {
                var eff = await router.ComputeEffectivity(dc);
                var sp  = await clusterClientFactory.CreateClusterClient(dc, ct);
                registry.Upsert(new(dc, eff, sp, DateTime.Now, CREATED, new()));
                logger.LogInformation("DC [{dc}] added with effectivity {effectivity}", dc, eff);
            }
            else if (existing.status is ONLINE)
                registry.Upsert(existing with
                {
                    lastSeen = DateTime.UtcNow
                });
        }

        foreach (var kv in registry.GetAll()
                    .Where(x => x.Value.status is OFFLINE)
                    .Where(x => DateTime.UtcNow - x.Value.lastSeen > _gcTimeout))
        {
            logger.LogWarning("DC [{dc}] removed after stale timeout", kv.Key);
            await kv.Value.ctSource.CancelAsync();
            (kv.Value.serviceProvider as IDisposable)?.Dispose();
            registry.Remove(kv.Key);
        }

        await Task.Delay(TimeSpan.FromSeconds(30), ct);
    }
}