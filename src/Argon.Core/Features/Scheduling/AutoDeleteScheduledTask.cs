namespace Argon.Features.Scheduling;

using Argon.Features.Logic;
using Argon.Grains.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Replaces AutoDeleteSchedulerGrain singleton.
/// Runs once per day, scans for inactive users, triggers auto-deletion.
/// Only one DC executes per interval (NATS WorkQueue).
/// </summary>
public sealed class AutoDeleteScheduledTask(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IGrainFactory grainFactory,
    IOptions<AccountDeletionOptions> options,
    ILogger<AutoDeleteScheduledTask> logger) : IScheduledTask
{
    private const int BatchSize = 100;
    private const int DefaultAutoDeleteMonths = 12;

    public string TaskName => "auto_delete_scan";
    public TimeSpan Interval => TimeSpan.FromHours(24);
    public TimeSpan InitialDelay => TimeSpan.FromMinutes(5);

    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.AutoDeleteEnabled)
        {
            logger.LogInformation("Auto-delete is disabled, skipping scan");
            return;
        }

        var processedCount = 0;
        var triggeredCount = 0;

        await using var ctx = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var offset = 0;
        bool hasMore;

        do
        {
            var candidates = await ctx.Users
                .AsNoTracking()
                .Where(u => !u.IsDeleted && !u.HasActiveUltima)
                .OrderBy(u => u.Id)
                .Skip(offset)
                .Take(BatchSize)
                .Select(u => new
                {
                    u.Id,
                    u.CreatedAt,
                    AutoDeleteMonths = ctx.AutoDeleteSettings
                        .Where(s => s.UserId == u.Id && s.Enabled)
                        .Select(s => s.Months)
                        .FirstOrDefault(),
                    LastLogin = ctx.DeviceHistories
                        .Where(d => d.UserId == u.Id)
                        .Max(d => (DateTimeOffset?)d.LastLoginTime)
                })
                .ToListAsync(ct);

            hasMore = candidates.Count == BatchSize;
            offset += candidates.Count;

            foreach (var candidate in candidates)
            {
                processedCount++;

                var thresholdMonths = candidate.AutoDeleteMonths is > 0
                    ? candidate.AutoDeleteMonths.Value
                    : DefaultAutoDeleteMonths;

                var lastActivity = candidate.LastLogin ?? candidate.CreatedAt;
                var inactiveDuration = now - lastActivity;
                var thresholdDuration = TimeSpan.FromDays(thresholdMonths * 30.44);

                if (inactiveDuration < thresholdDuration)
                    continue;

                try
                {
                    var grain = grainFactory.GetGrain<IAccountDeletionGrain>(candidate.Id);
                    var result = await grain.RequestAutoDeleteAsync();

                    if (result.Success)
                    {
                        triggeredCount++;
                        logger.LogInformation(
                            "Auto-delete triggered for user {UserId}, last activity: {LastActivity}, threshold: {Months}mo",
                            candidate.Id, lastActivity, thresholdMonths);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to trigger auto-delete for user {UserId}", candidate.Id);
                }
            }
        } while (hasMore && !ct.IsCancellationRequested);

        logger.LogInformation(
            "Auto-delete scan completed: processed {Processed}, triggered {Triggered}",
            processedCount, triggeredCount);
    }
}
