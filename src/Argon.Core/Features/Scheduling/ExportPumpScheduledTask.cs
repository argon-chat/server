namespace Argon.Features.Scheduling;

using Argon.Grains.Interfaces;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

/// <summary>
/// Replaces ExportPumpGrain singleton.
/// Periodically pings active export grains to keep them alive (grain timers die on deactivation).
/// Runs every 2 minutes. Only one DC executes per interval (NATS WorkQueue).
///
/// Other services call RegisterExport/UnregisterExport to track active exports.
/// </summary>
public sealed class ExportPumpScheduledTask(
    IGrainFactory grainFactory,
    ILogger<ExportPumpScheduledTask> logger) : IScheduledTask
{
    private readonly ConcurrentDictionary<Guid, byte> _activeExports = new();

    public string TaskName => "export_pump";
    public TimeSpan Interval => TimeSpan.FromMinutes(2);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(1);

    public void RegisterExport(Guid userId) => _activeExports.TryAdd(userId, 0);
    public void UnregisterExport(Guid userId) => _activeExports.TryRemove(userId, out _);

    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (_activeExports.IsEmpty)
        {
            logger.LogDebug("Export pump: no active exports");
            return;
        }

        logger.LogInformation("Export pump firing for {Count} active exports", _activeExports.Count);

        var toRemove = new List<Guid>();

        foreach (var userId in _activeExports.Keys)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var grain = grainFactory.GetGrain<IUserDataExportGrain>(userId);
                var inProgress = await grain.IsExportInProgressAsync();

                if (!inProgress)
                    toRemove.Add(userId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to pump export grain for user {UserId}", userId);
            }
        }

        foreach (var id in toRemove)
            _activeExports.TryRemove(id, out _);
    }
}

