namespace Argon.Grains;

using Argon.Features.Logic;
using Argon.Grains.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public class AutoDeleteSchedulerGrain(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IGrainFactory grainFactory,
    IOptions<AccountDeletionOptions> options,
    ILogger<AutoDeleteSchedulerGrain> logger)
    : Grain, IAutoDeleteSchedulerGrain, IRemindable
{
    private const string ReminderName = "auto-delete-scan";
    private const int BatchSize = 100;
    private const int DefaultAutoDeleteMonths = 12;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.AutoDeleteEnabled)
        {
            logger.LogInformation("Auto-delete is disabled, skipping reminder registration");
            return;
        }

        await this.RegisterOrUpdateReminder(
            ReminderName,
            dueTime: TimeSpan.FromMinutes(5),
            period: TimeSpan.FromHours(24));
    }

    public ValueTask EnsureSchedulerActiveAsync()
        => ValueTask.CompletedTask; // activation itself registers the reminder

    public async ValueTask RunScanAsync()
        => await ScanAndTriggerAsync();

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReminderName)
            return;

        if (!options.Value.AutoDeleteEnabled)
        {
            logger.LogInformation("Auto-delete is disabled, unregistering reminder");
            if (await this.GetReminder(ReminderName) is { } reminder)
                await this.UnregisterReminder(reminder);
            return;
        }

        logger.LogInformation("Auto-delete reminder fired, starting scan");

        try
        {
            await ScanAndTriggerAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-delete scan failed");
        }
    }

    private async Task ScanAndTriggerAsync()
    {
        var processedCount = 0;
        var triggeredCount = 0;

        await using var ctx = await dbFactory.CreateDbContextAsync();
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
                .ToListAsync();

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
        } while (hasMore);

        logger.LogInformation(
            "Auto-delete scan completed: processed {Processed}, triggered {Triggered}",
            processedCount, triggeredCount);
    }
}
