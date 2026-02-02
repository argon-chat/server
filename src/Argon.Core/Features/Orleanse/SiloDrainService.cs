namespace Argon.Drains;

using Orleans.Runtime;
using Lock = System.Threading.Lock;

/// <summary>
/// Represents the current state of the silo for blue-green deployments.
/// </summary>
public enum SiloDrainState
{
    Active,
    Draining,
    Drained,
    ShuttingDown
}

/// <summary>
/// Status information about the silo drain state.
/// </summary>
public record SiloStatus(
    SiloDrainState State,
    bool IsDraining,
    int ActiveGrainCount,
    DateTime? DrainStartedAt,
    TimeSpan? DrainDuration,
    string SiloAddress,
    Orleans.Runtime.SiloStatus OrleansSiloStatus);

/// <summary>
/// Result of a drain operation.
/// </summary>
public record DrainOperationResult(bool IsSuccess, string Message);

/// <summary>
/// Service interface for managing Orleans silo draining during blue-green deployments.
/// </summary>
public interface ISiloDrainService
{
    /// <summary>
    /// Starts draining the silo - marks as not ready for K8s and waits for grains to deactivate.
    /// </summary>
    Task<DrainOperationResult> StartDrainingAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the current drain status.
    /// </summary>
    SiloStatus GetStatus();

    /// <summary>
    /// Initiates graceful shutdown - Orleans will handle membership status correctly.
    /// </summary>
    Task<DrainOperationResult> InitiateShutdownAsync(CancellationToken ct = default);

    /// <summary>
    /// Cancels an in-progress drain operation.
    /// </summary>
    DrainOperationResult CancelDraining();
}

/// <summary>
/// Implementation of silo drain service for blue-green deployments.
/// 
/// Key principle: Don't manipulate Orleans membership table directly!
/// Orleans expects to manage its own membership lifecycle.
/// 
/// Instead, we:
/// 1. Mark silo as "draining" internally (affects K8s readiness probe)
/// 2. K8s removes pod from Service endpoints (no new traffic)
/// 3. Wait for existing grains to deactivate naturally
/// 4. Call StopApplication() - Orleans handles graceful shutdown correctly
/// </summary>
public class SiloDrainService(
    ILocalSiloDetails localSiloDetails,
    IHostApplicationLifetime appLifetime,
    ISiloStatusOracle siloStatusOracle,
    IActivationWorkingSet activationWorkingSet,
    ILogger<SiloDrainService> logger) : ISiloDrainService
{
    private volatile SiloDrainState _state = SiloDrainState.Active;
    private DateTime? _drainStartedAt;
    private readonly Lock _lock = new();

    public async Task<DrainOperationResult> StartDrainingAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_state != SiloDrainState.Active)
            {
                return new DrainOperationResult(false, $"Cannot start draining. Current state: {_state}");
            }

            _state = SiloDrainState.Draining;
            _drainStartedAt = DateTime.UtcNow;
        }

        var siloAddress = localSiloDetails.SiloAddress;
        logger.LogWarning("Starting drain for silo {SiloAddress}. Readiness probe will now return unhealthy.", siloAddress);

        try
        {
            // At this point K8s readiness probe returns unhealthy (503)
            // K8s will remove this pod from Service endpoints
            // No new requests will be routed to this silo
            
            logger.LogInformation("Waiting for K8s to remove pod from endpoints and for in-flight requests to complete...");
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

            // Wait for grains to deactivate naturally (by idle timeout)
            var drainTimeout = TimeSpan.FromMinutes(5);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(drainTimeout);

            await WaitForGrainsToDeactivateAsync(timeoutCts.Token);

            lock (_lock)
            {
                _state = SiloDrainState.Drained;
            }

            var remainingGrains = GetActiveGrainCount();
            logger.LogWarning("Silo {SiloAddress} drained. Remaining grains: {Count}", siloAddress, remainingGrains);
            
            return new DrainOperationResult(true, 
                $"Silo drained successfully. Remaining grains: {remainingGrains}. Ready for shutdown.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("Drain operation was cancelled by user");
            lock (_lock)
            {
                _state = SiloDrainState.Active;
                _drainStartedAt = null;
            }
            return new DrainOperationResult(false, "Drain operation was cancelled");
        }
        catch (OperationCanceledException)
        {
            // Timeout - mark as drained anyway, shutdown will handle remaining grains
            var remainingGrains = GetActiveGrainCount();
            logger.LogWarning("Drain timeout. Remaining grains: {Count}. Marking as drained.", remainingGrains);
            lock (_lock)
            {
                _state = SiloDrainState.Drained;
            }
            return new DrainOperationResult(true, 
                $"Drain timed out with {remainingGrains} remaining grains. Ready for shutdown.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during drain operation");
            lock (_lock)
            {
                _state = SiloDrainState.Active;
                _drainStartedAt = null;
            }
            return new DrainOperationResult(false, $"Drain failed: {ex.Message}");
        }
    }

    public SiloStatus GetStatus()
    {
        var activeGrains = GetActiveGrainCount();
        var drainDuration = _drainStartedAt.HasValue
            ? DateTime.UtcNow - _drainStartedAt.Value
            : (TimeSpan?)null;

        return new SiloStatus(
            _state,
            _state is SiloDrainState.Draining,
            activeGrains,
            _drainStartedAt,
            drainDuration,
            localSiloDetails.SiloAddress.ToString(),
            siloStatusOracle.CurrentStatus);
    }

    public async Task<DrainOperationResult> InitiateShutdownAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_state is not (SiloDrainState.Drained or SiloDrainState.Draining))
            {
                return new DrainOperationResult(false,
                    $"Cannot shutdown. Silo must be drained first. Current state: {_state}");
            }

            _state = SiloDrainState.ShuttingDown;
        }

        var siloAddress = localSiloDetails.SiloAddress;
        logger.LogWarning("Initiating graceful shutdown for silo {SiloAddress}", siloAddress);

        // Give a moment for any last requests
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        // Let Orleans handle the shutdown properly - it will:
        // 1. Stop accepting new grain activations
        // 2. Deactivate existing grains gracefully
        // 3. Update membership table correctly (Stopping -> Dead)
        // 4. Notify other silos
        appLifetime.StopApplication();

        return new DrainOperationResult(true, "Graceful shutdown initiated. Orleans will handle membership correctly.");
    }

    public DrainOperationResult CancelDraining()
    {
        lock (_lock)
        {
            if (_state != SiloDrainState.Draining)
            {
                return new DrainOperationResult(false, $"Cannot cancel. Current state: {_state}");
            }

            _state = SiloDrainState.Active;
            _drainStartedAt = null;
        }

        logger.LogWarning("Drain cancelled for silo {SiloAddress}. Readiness probe will now return healthy.", 
            localSiloDetails.SiloAddress);

        return new DrainOperationResult(true, "Drain cancelled. Silo is accepting traffic again.");
    }

    /// <summary>
    /// Waits for grains to deactivate naturally by their idle timeout.
    /// </summary>
    private async Task WaitForGrainsToDeactivateAsync(CancellationToken ct)
    {
        var checkInterval = TimeSpan.FromSeconds(5);
        var stableIterations = 0;
        var previousCount = GetActiveGrainCount();

        logger.LogInformation("Waiting for grains to deactivate. Initial count: {Count}", previousCount);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(checkInterval, ct);

            var currentCount = GetActiveGrainCount();
            logger.LogInformation("Grain deactivation progress: {Current} grains remaining (was {Previous})",
                currentCount, previousCount);

            // Consider drained if very few grains remain
            if (currentCount <= 5)
            {
                logger.LogInformation("Grain count low enough ({Count}), considering silo drained", currentCount);
                break;
            }

            // If count is stable for 3 iterations, assume natural deactivation is done
            if (currentCount == previousCount)
            {
                stableIterations++;
                if (stableIterations >= 3)
                {
                    logger.LogInformation("Grain count stable at {Count} for {Iterations} iterations", 
                        currentCount, stableIterations);
                    break;
                }
            }
            else
            {
                stableIterations = 0;
            }

            previousCount = currentCount;
        }
    }

    /// <summary>
    /// Gets the count of active grains on this silo.
    /// </summary>
    private int GetActiveGrainCount()
    {
        try
        {
            return activationWorkingSet.Count;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get grain count");
            return 0;
        }
    }
}
