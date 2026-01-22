namespace Argon.Features;

using Argon;
using System.Diagnostics.Metrics;
using Orleans.Placement.Rebalancing;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using static Math;

public class ArgonRebalancerBackoffProvider : IFailedSessionBackoffProvider
{
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
    private const int BaseDelaySeconds = 10;

    public TimeSpan Next(int attempt)
    {
        var delaySeconds = Min(BaseDelaySeconds * Pow(2, attempt - 1), MaxDelay.TotalSeconds);
        var delay = TimeSpan.FromSeconds(delaySeconds);
        return delay > MinDelay ? delay : MinDelay;
    }
}

public sealed class ArgonImbalanceToleranceRule : 
    IImbalanceToleranceRule, 
    ILifecycleParticipant<ISiloLifecycle>,
    ILifecycleObserver,
    ISiloStatusListener
{
    private readonly ISiloStatusOracle _siloOracle;
    private readonly ILogger<ArgonImbalanceToleranceRule> _logger;
    private readonly ArgonRebalancingOptions _options;
    
    private readonly Counter<long> _rebalanceChecksCounter;
    private readonly Counter<long> _rebalanceAcceptedCounter;
    private readonly Counter<long> _rebalanceRejectedCounter;
    private readonly Histogram<double> _imbalanceHistogram;
    
    private readonly Lock _lock = new();
    private readonly List<ImbalanceSnapshot> _history = new();
    private int _activeSiloCount;
    private DateTime _lastRebalanceTime = DateTime.MinValue;
    private uint _lastImbalance;

    public ArgonImbalanceToleranceRule(
        ISiloStatusOracle siloOracle,
        IConfiguration configuration,
        ILogger<ArgonImbalanceToleranceRule> logger)
    {
        _siloOracle = siloOracle;
        _logger = logger;
        _options = configuration.GetSection("Orleans:Rebalancing").Get<ArgonRebalancingOptions>() 
                   ?? new ArgonRebalancingOptions();

        var meter = Argon.Instruments.Meter;
        _rebalanceChecksCounter = meter.CreateCounter<long>(
            InstrumentNames.OrleansRebalanceChecks,
            description: "Total number of rebalance checks performed");
        
        _rebalanceAcceptedCounter = meter.CreateCounter<long>(
            InstrumentNames.OrleansRebalanceAccepted,
            description: "Number of times rebalancing was accepted");
        
        _rebalanceRejectedCounter = meter.CreateCounter<long>(
            InstrumentNames.OrleansRebalanceRejected,
            description: "Number of times rebalancing was rejected");
        
        _imbalanceHistogram = meter.CreateHistogram<double>(
            InstrumentNames.OrleansImbalanceValue,
            unit: "activations",
            description: "Distribution of imbalance values");
    }

    public bool IsSatisfiedBy(uint imbalance)
    {
        _rebalanceChecksCounter.Add(1);
        _imbalanceHistogram.Record(imbalance);
        
        lock (_lock)
        {
            _lastImbalance = imbalance;
            var now = DateTime.UtcNow;
            
            if (now - _lastRebalanceTime < _options.MinRebalanceInterval)
            {
                _rebalanceRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "cooldown"));
                _logger.LogDebug("Rebalance rejected: cooldown period active (last: {LastRebalance})", 
                    _lastRebalanceTime);
                return false;
            }

            var threshold = CalculateDynamicThreshold();
            var isSatisfied = imbalance <= threshold;

            if (isSatisfied)
            {
                _rebalanceAcceptedCounter.Add(1);
                _lastRebalanceTime = now;
                
                _history.Add(new ImbalanceSnapshot(now, imbalance, threshold, true));
                TrimHistory();
                
                _logger.LogInformation(
                    "Rebalance ACCEPTED: imbalance={Imbalance} <= threshold={Threshold} (silos={SiloCount}, trend={Trend})",
                    imbalance, threshold, _activeSiloCount, CalculateTrend());
            }
            else
            {
                _rebalanceRejectedCounter.Add(1, new KeyValuePair<string, object?>("reason", "threshold"));
                
                _history.Add(new ImbalanceSnapshot(now, imbalance, threshold, false));
                TrimHistory();
                
                _logger.LogDebug(
                    "Rebalance REJECTED: imbalance={Imbalance} > threshold={Threshold} (silos={SiloCount})",
                    imbalance, threshold, _activeSiloCount);
            }

            return isSatisfied;
        }
    }

    private uint CalculateDynamicThreshold()
    {
        var baseThreshold = _options.BaseImbalanceThreshold;
        
        if (_activeSiloCount <= 1)
            return baseThreshold;

        var clusterScaleFactor = 1.0 + (_activeSiloCount - 1) * _options.ClusterSizeScalingFactor;
        var trendAdjustment = CalculateTrendAdjustment();
        
        var threshold = baseThreshold * clusterScaleFactor * trendAdjustment;
        
        return (uint)Max(_options.MinThreshold, Min(_options.MaxThreshold, threshold));
    }

    private double CalculateTrendAdjustment()
    {
        if (_history.Count < 3)
            return 1.0;

        var recentHistory = _history.TakeLast(5).ToList();
        var trend = CalculateTrend();

        if (trend > _options.ImbalanceIncreasingThreshold)
        {
            return 1.0 - _options.TrendSensitivity;
        }
        else if (trend < -_options.ImbalanceDecreasingThreshold)
        {
            return 1.0 + _options.TrendSensitivity;
        }

        return 1.0;
    }

    private double CalculateTrend()
    {
        if (_history.Count < 2)
            return 0;

        var recent = _history.TakeLast(Math.Min(5, _history.Count)).ToList();
        if (recent.Count < 2)
            return 0;

        var first = recent[0].Imbalance;
        var last = recent[^1].Imbalance;
        
        return first > 0 ? (double)(last - first) / first : 0;
    }

    private void TrimHistory()
    {
        var cutoff = DateTime.UtcNow - _options.HistoryRetentionPeriod;
        _history.RemoveAll(s => s.Timestamp < cutoff);
        
        if (_history.Count > _options.MaxHistorySize)
        {
            _history.RemoveRange(0, _history.Count - _options.MaxHistorySize);
        }
    }

    public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
    {
        lock (_lock)
        {
            _activeSiloCount = _siloOracle
                .GetApproximateSiloStatuses(true)
                .Count(s => s.Value == SiloStatus.Active);

            _logger.LogInformation(
                "Silo status changed: {Silo} -> {Status}, active silos: {ActiveCount}",
                updatedSilo, status, _activeSiloCount);
        }
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(ArgonImbalanceToleranceRule),
            ServiceLifecycleStage.ApplicationServices,
            this);
    }

    public Task OnStart(CancellationToken cancellationToken = default)
    {
        _siloOracle.SubscribeToSiloStatusEvents(this);
        
        _activeSiloCount = _siloOracle
            .GetApproximateSiloStatuses(true)
            .Count(s => s.Value == SiloStatus.Active);
        
        _logger.LogInformation(
            "ArgonImbalanceToleranceRule started with {ActiveSilos} active silos, options: {@Options}",
            _activeSiloCount, _options);
        
        return Task.CompletedTask;
    }

    public Task OnStop(CancellationToken cancellationToken = default)
    {
        _siloOracle.UnSubscribeFromSiloStatusEvents(this);
        
        _logger.LogInformation(
            "ArgonImbalanceToleranceRule stopped. Final stats: {TotalChecks} checks, last imbalance: {LastImbalance}",
            _history.Count, _lastImbalance);
        
        return Task.CompletedTask;
    }

    private readonly record struct ImbalanceSnapshot(
        DateTime Timestamp,
        uint Imbalance,
        uint Threshold,
        bool Accepted);
}

public sealed class ArgonRebalancingOptions
{
    public uint BaseImbalanceThreshold { get; init; } = 15;
    
    public uint MinThreshold { get; init; } = 5;
    
    public uint MaxThreshold { get; init; } = 100;
    
    public double ClusterSizeScalingFactor { get; init; } = 0.15;
    
    public TimeSpan MinRebalanceInterval { get; init; } = TimeSpan.FromSeconds(30);
    
    public TimeSpan HistoryRetentionPeriod { get; init; } = TimeSpan.FromMinutes(15);
    
    public int MaxHistorySize { get; init; } = 100;
    
    public double ImbalanceIncreasingThreshold { get; init; } = 0.3;
    
    public double ImbalanceDecreasingThreshold { get; init; } = 0.3;
    
    public double TrendSensitivity { get; init; } = 0.2;}