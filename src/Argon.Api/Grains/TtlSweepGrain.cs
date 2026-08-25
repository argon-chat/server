namespace Argon.Grains;

using Argon.Features.Clustering;
using Argon.Features.EF;
using Argon.Grains.Interfaces;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Runs <see cref="TtlSweeper"/> on a reminder, and publishes what it found.
/// </summary>
/// <remarks>
/// <para>Deliberately thin. Everything that decides <em>which</em> rows die lives in
/// <see cref="TtlSweepTargets"/>, which is a pure function of the EF model and can therefore be
/// asserted in the fast suite with no database anywhere near it. What is left here is the part that
/// genuinely needs a silo: a schedule, a connection, and somewhere to put the verdict.</para>
///
/// <para><b>The connection is opened and pinned</b> for the pass, the same way the boot path does it,
/// because the lease and the delete batches have to run on one session — a lease renewed on one pooled
/// connection and a <c>DELETE</c> issued on another is a lease that protects nothing. It is released
/// when the context is disposed at the end of the pass, not held between reminder ticks.</para>
/// </remarks>
public class TtlSweepGrain(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration configuration,
    TtlSweepState state,
    RoleDescriptor role,
    ILogger<TtlSweepGrain> logger)
    : Grain, ITtlSweepGrain, IRemindable
{
    private const string ReminderName = "ttl-sweep";

    /// <summary>
    /// Long enough that a pod restart does not sweep, short enough that a fresh cluster does before
    /// anyone gives up waiting.
    /// </summary>
    /// <remarks>
    /// Five minutes, matching <c>AutoDeleteSchedulerGrain</c>. A hard restart of the fleet brings dozens
    /// of silos up at once and the first minutes are the worst possible time to add scan load to the
    /// primary; there is nothing time-critical about a row that expired an hour ago.
    /// </remarks>
    private static readonly TimeSpan FirstSweepDelay = TimeSpan.FromMinutes(5);

    private TtlSweepOptions Options => TtlSweepOptions.FromConfiguration(configuration);

    /// <summary>
    /// Registers the reminder — unless the sweeper is off, in which case it makes sure there is none.
    /// </summary>
    /// <remarks>
    /// Read on every activation rather than cached, because a reminder is persistent: one registered by
    /// a pod that has since been replaced keeps firing against configuration nobody can see any more.
    /// Turning <c>Mode</c> to <c>Off</c> has to be able to actually stop it, which means unregistering
    /// and not merely returning early from the tick.
    /// </remarks>
    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (Options.Mode is TtlSweepMode.Off)
        {
            await StopReminderAsync();
            return;
        }

        await this.RegisterOrUpdateReminder(ReminderName, FirstSweepDelay, Options.Interval);
    }

    public ValueTask EnsureSweeperActiveAsync()
        => ValueTask.CompletedTask; // activation itself registers the reminder

    public async ValueTask RunSweepAsync()
        => await SweepAsync(CancellationToken.None);

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReminderName)
            return;

        if (Options.Mode is TtlSweepMode.Off)
        {
            logger.LogInformation("The TTL sweeper is off; unregistering its reminder");
            await StopReminderAsync();
            return;
        }

        await SweepAsync(CancellationToken.None);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            await db.Database.OpenConnectionAsync(ct);

            state.Publish(await TtlSweeper.RunAsync(
                db, db.Database.GetDbConnection(), Options, role.Id.Value, logger, ct));
        }
        catch (Exception e)
        {
            // Never rethrown. An exception out of ReceiveReminder is retried by Orleans, and retrying a
            // pass whose statements delete rows is exactly the thing the sweeper refuses to do to
            // itself — the next tick re-derives everything from the catalog anyway, which is the same
            // recovery with an hour of distance. The verdict still travels: a faulted pass is published,
            // counted, and degrades the diagnostic health check.
            logger.LogError(e, "TTL sweep failed; the next tick will re-derive from scratch");
            state.Publish(TtlSweepReport.Faulted(e));
        }
    }

    private async Task StopReminderAsync()
    {
        if (await this.GetReminder(ReminderName) is { } reminder)
            await this.UnregisterReminder(reminder);
    }
}
