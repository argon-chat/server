namespace Argon.Api.Features;

using System.Collections.Concurrent;
using k8s;
using Orleans.Placement.Repartitioning;
using static Math;
using static SiloStatus;

public static class KubeExtensions
{
    public static IServiceCollection AddKubeResources(this IServiceCollection services)
    {
        services.AddHostedService<KubePolling>();
        services.AddSingleton<IKubeResources, KubeResources>();
        return services;
    }
}

public class KubePolling(IKubeResources resources) : BackgroundService
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Yield();
            await resources.FetchAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

public class KubeResources(IHostEnvironment env, IKubernetes client) : IKubeResources
{
    public double    AverageCpuLoad { get; private set; }

    public async ValueTask FetchAsync()
    {
        AverageCpuLoad = await GetAvgCpu();
    }

    private async Task<double> GetAvgCpu()
    {
        if (!env.IsProduction())
            return 50;

        var metrics   = await client.GetKubernetesNodesMetricsAsync();
        var nodeCount = metrics.Items.Count();

        var totalCpu = metrics.Items
           .Select(node => node.Usage["cpu"])
           .Select(cpuUsageStr => int.Parse((string)cpuUsageStr.Value.Replace("m", "")))
           .Aggregate<int, double>(0, (current, cpuUsage) => current + cpuUsage);

        return totalCpu / nodeCount;
    }
}
public interface IKubeResources
{
    double AverageCpuLoad { get; }


    ValueTask FetchAsync();
}

public class BalanceRule(ISiloStatusOracle oracle, IConfiguration config, IKubeResources kubeResources) :
    IImbalanceToleranceRule, ILifecycleParticipant<ISiloLifecycle>, ILifecycleObserver, ISiloStatusListener
{
    private readonly object                                        guarder = new();
    private readonly ConcurrentDictionary<SiloAddress, SiloStatus> silos   = new();

    private          ulong    ImbalanceDelta;
    private readonly double   Base                 = double.Parse(config["clustering:base"] ?? "10");
    private readonly TimeSpan MinRebalanceInterval = TimeSpan.FromMinutes(0.5);
    private          DateTime lastRebalanceTime;

    public bool IsSatisfiedBy(uint imbalance)
    {
        var currentTime = DateTime.UtcNow;
        if (currentTime - lastRebalanceTime < MinRebalanceInterval)
            return false;
        return imbalance <= Interlocked.Read(ref ImbalanceDelta);
    }

    public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
    {
        silos.AddOrUpdate(updatedSilo, static (_, arg) => arg, static (_, _, arg) => arg, status);

        var currentActive = silos.Count(s => s.Value == Active);
        var loadFactor    = kubeResources.AverageCpuLoad;

        var result = Floor(Max(Base, 100 / (1 + loadFactor + 0.1 * currentActive)));

        var smoothingFactor = 0.1;
        var previousDelta   = Interlocked.Read(ref ImbalanceDelta);
        var newDelta        = (1 - smoothingFactor) * previousDelta + smoothingFactor * result;

        Interlocked.Exchange(ref ImbalanceDelta, (ulong)newDelta);
    }

    public void Participate(ISiloLifecycle lifecycle)
        => lifecycle.Subscribe(nameof(BalanceRule), ServiceLifecycleStage.ApplicationServices, this);

    public Task OnStart(CancellationToken cancellationToken = default)
    {
        oracle.SubscribeToSiloStatusEvents(this);
        return Task.CompletedTask;
    }

    public Task OnStop(CancellationToken cancellationToken = default)
    {
        oracle.UnSubscribeFromSiloStatusEvents(this);
        return Task.CompletedTask;
    }
}