namespace Argon.Features;

using System.Globalization;
using Env;
using k8s;
using Orleans.Placement.Repartitioning;
using static Math;
using static SiloStatus;

public static class KubeExtensions
{
    public static IServiceCollection AddKubeResources(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        if (builder.Environment.IsKube())
        {
            var config = KubernetesClientConfiguration.InClusterConfig();
            services.AddSingleton(config);
            services.AddSingleton<IKubernetes>(new Kubernetes(config));
        }

        services.AddHostedService<KubePolling>();
        services.AddSingleton<IKubeResources, KubeResources>();

        return services;
    }
}

public class KubePolling(IKubeResources resources) : BackgroundService
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return; // todo
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Yield();
            await resources.FetchAsync();
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

public class KubeResources(IHostEnvironment env, IServiceProvider serviceProvider, ILogger<IKubeResources> logger) : IKubeResources
{
    public double AverageCpuLoad { get; private set; } = 10;

    public async ValueTask FetchAsync()
        => AverageCpuLoad = await GetAvgCpu();

    private async Task<double> GetAvgCpu()
    {
        if (!env.IsKube())
            return 10;
        await using var
            scope = serviceProvider.CreateAsyncScope();
        var client    = scope.ServiceProvider.GetRequiredService<IKubernetes>();
        var metrics   = await client.GetKubernetesNodesMetricsAsync();
        var nodeCount = metrics.Items.Count();

        var totalCpu = metrics.Items
           .Select(node => node.Usage["cpu"])
           .Select(cpuUsageStr => ParseCpuUsage(cpuUsageStr.Value))
           .Aggregate<double, double>(0, (current, cpuUsage) => current + cpuUsage);


        logger.LogInformation("Silo usable '{totalCpu}' cpu", totalCpu);

        return totalCpu / nodeCount;
    }

    static double ParseCpuUsage(string cpuUsageStr)
    {
        if (cpuUsageStr.EndsWith("n"))
            return double.Parse(cpuUsageStr.Replace("n", ""), CultureInfo.InvariantCulture) / 1_000_000;
        if (cpuUsageStr.EndsWith("u"))
            return double.Parse(cpuUsageStr.Replace("u", ""), CultureInfo.InvariantCulture) / 1_000;
        if (cpuUsageStr.EndsWith("m"))
            return double.Parse(cpuUsageStr.Replace("m", ""), CultureInfo.InvariantCulture);
        var cores = double.Parse(cpuUsageStr, CultureInfo.InvariantCulture);
        return cores * 1000;
    }
}

public interface IKubeResources
{
    double AverageCpuLoad { get; }


    ValueTask FetchAsync();
}

public class BalanceRule(ISiloStatusOracle oracle, IConfiguration config, IKubeResources kubeResources, ILogger<IImbalanceToleranceRule> logger) :
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
        lastRebalanceTime = DateTime.UtcNow;
        var delta  = Interlocked.Read(ref ImbalanceDelta);
        var result = imbalance <= delta;
        logger.LogInformation("Imbalance value '{imbalance}' <= '{imbalanceDelta}'", result, delta);
        return result;
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