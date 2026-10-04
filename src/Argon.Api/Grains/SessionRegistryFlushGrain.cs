namespace Argon.Grains;

using Argon.Features.Auth;
using Argon.Grains.Interfaces;
using Argon.Services;

public class SessionRegistryFlushGrain(
    SessionRegistryStore store,
    ISessionRegistryTransit transit,
    IArgonCacheDatabase cache,
    IOptions<SessionRegistryOptions> options,
    ILogger<SessionRegistryFlushGrain> logger)
    : Grain, ISessionRegistryFlushGrain, IRemindable, IReminderJob
{
    private const string ReminderName = "session-registry-flush";

    private DateTimeOffset lastSweepAt = DateTimeOffset.MinValue;

    string IReminderJob.ReminderName => ReminderName;

    ReminderSchedule? IReminderJob.Schedule
        => new(options.Value.FlushInterval, options.Value.FlushInterval);

    public ValueTask EnsureActiveAsync()
        => ValueTask.CompletedTask;

    public async ValueTask<int> RunFlushAsync()
        => await FlushAsync(CancellationToken.None);

    public async ValueTask<int> RunSweepAsync()
        => await SweepAsync(CancellationToken.None);

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReminderName)
            return;

        await FlushAsync(CancellationToken.None);

        var o = options.Value;

        if (o.StaleAfter > TimeSpan.Zero && DateTimeOffset.UtcNow - lastSweepAt >= o.SweepInterval)
            await SweepAsync(CancellationToken.None);
    }

    private async Task<int> FlushAsync(CancellationToken ct)
    {
        var o       = options.Value;
        var flushed = 0;

        try
        {
            while (flushed < o.MaxFlushPerTick)
            {
                var keys = await transit.DirtyAsync(o.FlushBatch, ct);

                if (keys.Count == 0)
                    break;

                var batch    = new List<(SessionRegistryKey Key, SessionRegistryRecord Record)>(keys.Count);
                var revoked  = new Dictionary<Guid, HashSet<string>>();

                foreach (var key in keys)
                {
                    var record = await transit.HotAsync(key, ct);

                    if (record is null)
                    {
                        await transit.TryClearDirtyAsync(key, 0, ct);
                        continue;
                    }

                    if (!revoked.TryGetValue(key.UserId, out var tombstones))
                        revoked[key.UserId] = tombstones = [.. await cache.SetMembersAsync(SessionRevocation.RevokedKey(key.UserId), ct)];

                    if (tombstones.Contains(key.CredentialSessionId.ToString()))
                        record.Deleted = true;

                    batch.Add((key, record));
                }

                if (batch.Count > 0)
                {
                    await store.UpsertAsync(batch.Select(x => x.Record).ToList(), ct);

                    foreach (var (key, record) in batch)
                        await transit.TryClearDirtyAsync(key, record.Version, ct);
                }

                flushed += keys.Count;

                if (keys.Count < o.FlushBatch)
                    break;
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Session registry flush failed after {Count} records; the rest stay dirty for the next tick", flushed);
        }

        return flushed;
    }

    private async Task<int> SweepAsync(CancellationToken ct)
    {
        var o = options.Value;

        lastSweepAt = DateTimeOffset.UtcNow;

        if (o.StaleAfter <= TimeSpan.Zero)
            return 0;

        try
        {
            var swept = await store.SweepStaleAsync(DateTimeOffset.UtcNow - o.StaleAfter, o.SweepBatch, ct);

            if (swept > 0)
                logger.LogInformation("Signed out {Count} device(s) not heard from in {StaleAfter}", swept, o.StaleAfter);

            return swept;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Session registry sweep failed; the next tick retries");
            return 0;
        }
    }
}
