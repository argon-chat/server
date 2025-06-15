namespace Argon.Api.Features;

using global::Orleans.Concurrency;
using Metrics;
using OrleansDashboard;
using OrleansDashboard.Metrics.Details;
using OrleansDashboard.Metrics.History;
using OrleansDashboard.Metrics.TypeFormatting;
using OrleansDashboard.Model;
using OrleansDashboard.Model.History;

public class OrleansMetricCollector(
    IGrainFactory grainFactory,
    ISiloDetailsProvider siloDetailsProvider,
    ISiloGrainClient siloGrainClient,
    IMetricsCollector metrics,
    ILocalSiloDetails localSiloDetails,
    ILogger<OrleansMetricCollector> logger) : BackgroundService
{
    private readonly ITraceHistory     history   = new TraceHistoryV2(256);
    private readonly DashboardCounters counters  = new(256);
    private readonly DateTime          startTime = DateTime.UtcNow;


    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureCountersAreUpToDate();
                await SendMetricsAsync();
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed generate orleans metric");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private static readonly MeasurementId OrleansTotalActivations = new("orleans_total_activations");
    private static readonly MeasurementId OrleansTotalHosts       = new("orleans_total_hosts");
    private static readonly MeasurementId OrleansGrainActivations = new("orleans_grain_activations");
    private static readonly MeasurementId OrleansGrainAvgLatency  = new("orleans_grain_avg_latency");
    private static readonly MeasurementId OrleansGrainMethodCalls = new("orleans_grain_method_calls");
    private static readonly MeasurementId OrleansGrainExceptions  = new("orleans_grain_exceptions");

    private async Task SendMetricsAsync()
    {
        var siloName = localSiloDetails.Name;

        await metrics.ObserveAsync(OrleansTotalActivations, counters.TotalActivationCount, new()
        {
            ["silo"] = siloName
        });

        await metrics.ObserveAsync(OrleansTotalHosts, counters.TotalActiveHostCount, new()
        {
            ["silo"] = siloName
        });

        foreach (var stat in counters.SimpleGrainStats)
        {
            var grainType = stat.GrainType;
            var silo      = stat.SiloAddress;

            await metrics.ObserveAsync(OrleansGrainActivations, stat.ActivationCount, new()
            {
                ["grainType"] = grainType,
                ["silo"]      = silo
            });

            if (stat.TotalCalls > 0)
            {
                await metrics.ObserveAsync(OrleansGrainAvgLatency, stat.TotalAwaitTime / stat.TotalCalls, new()
                {
                    ["grainType"] = grainType,
                    ["silo"]      = silo
                });

                await metrics.CountAsync(OrleansGrainMethodCalls, stat.TotalCalls, new()
                {
                    ["grainType"] = grainType,
                    ["silo"]      = silo
                });
            }

            if (stat.TotalExceptions > 0)
            {
                await metrics.ObserveAsync(OrleansGrainExceptions, stat.TotalExceptions, new()
                {
                    ["grainType"] = grainType,
                    ["silo"]      = silo
                });
            }
        }
    }


    internal void RecalculateCounters(int activationCount, SiloDetails[] hosts,
        IList<SimpleGrainStatistic> simpleGrainStatistics)
    {
        counters.TotalActivationCount = activationCount;

        counters.TotalActiveHostCount = hosts.Count(x => x.SiloStatus == SiloStatus.Active);
        counters.TotalActivationCountHistory =
            counters.TotalActivationCountHistory.Enqueue(activationCount).Dequeue();
        counters.TotalActiveHostCountHistory =
            counters.TotalActiveHostCountHistory.Enqueue(counters.TotalActiveHostCount).Dequeue();

        var elapsedTime = Math.Min((DateTime.UtcNow - startTime).TotalSeconds, 100);

        counters.Hosts = hosts;

        var aggregatedTotals = history.GroupByGrainAndSilo().ToLookup(x => (x.Grain, x.SiloAddress));

        counters.SimpleGrainStats = simpleGrainStatistics.Select(x =>
        {
            var grainName   = TypeFormatter.Parse(x.GrainType);
            var siloAddress = x.SiloAddress.ToParsableString();

            var result = new SimpleGrainStatisticCounter
            {
                ActivationCount = x.ActivationCount,
                GrainType       = grainName,
                SiloAddress     = siloAddress,
                TotalSeconds    = elapsedTime,
            };

            foreach (var item in aggregatedTotals[(grainName, siloAddress)])
            {
                result.TotalAwaitTime  += item.ElapsedTime;
                result.TotalCalls      += item.Count;
                result.TotalExceptions += item.ExceptionCount;
            }

            return result;
        }).ToArray();
    }

   

    private async Task EnsureCountersAreUpToDate()
    {
        var metricsGrain         = grainFactory.GetGrain<IManagementGrain>(0);
        var activationCountTask  = metricsGrain.GetTotalActivationCount();
        var simpleGrainStatsTask = metricsGrain.GetSimpleGrainStatistics();
        var siloDetailsTask      = siloDetailsProvider.GetSiloDetails();

        await Task.WhenAll(activationCountTask, simpleGrainStatsTask, siloDetailsTask);
        RecalculateCounters(activationCountTask.Result, siloDetailsTask.Result, simpleGrainStatsTask.Result);
    }

    public async Task<Immutable<DashboardCounters>> GetCounters()
    {
        await EnsureCountersAreUpToDate();

        return counters.AsImmutable();
    }
    public async Task<Immutable<Dictionary<string, Dictionary<string, GrainTraceEntry>>>> GetGrainTracing(
        string grain)
    {
        await EnsureCountersAreUpToDate();

        return history.QueryGrain(grain).AsImmutable();
    }

    public async Task<Immutable<Dictionary<string, GrainTraceEntry>>> GetClusterTracing()
    {
        await EnsureCountersAreUpToDate();

        return history.QueryAll().AsImmutable();
    }

    public async Task<Immutable<Dictionary<string, GrainMethodAggregate[]>>> TopGrainMethods(int take)
    {
        await EnsureCountersAreUpToDate();

        var values = history.AggregateByGrainMethod().ToList();

        GrainMethodAggregate[] GetTotalCalls()
            => values.OrderByDescending(x => x.Count).Take(take).ToArray();

        GrainMethodAggregate[] GetLatency()
            => values.OrderByDescending(x => x.Count).Take(take).ToArray();

        GrainMethodAggregate[] GetErrors()
            => values.Where(x => x.ExceptionCount > 0 && x.Count > 0)
               .OrderByDescending(x => x.ExceptionCount / x.Count).Take(take).ToArray();

        var result = new Dictionary<string, GrainMethodAggregate[]>
        {
            { "calls", GetTotalCalls() },
            { "latency", GetLatency() },
            { "errors", GetErrors() },
        };

        return result.AsImmutable();
    }

}