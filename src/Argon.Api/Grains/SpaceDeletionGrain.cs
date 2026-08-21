namespace Argon.Grains;

using ArgonContracts;
using Argon.Services;
using Microsoft.EntityFrameworkCore;
using Orleans.Providers;
using Persistence.States;

/// <summary>
/// Schedules and carries out the deletion of one space. Keyed by the space id.
/// </summary>
/// <remarks>
/// <para>The grace period is the whole design. Nothing here deletes on request; it writes down when
/// the deletion will happen, tells the space, and re-checks on a timer. Until that moment
/// <see cref="CancelAsync"/> undoes it completely.</para>
///
/// <para>A timer rather than a reminder, matching <c>AccountDeletionGrain</c>: the state carries the
/// deadline, so an activation that missed its window catches up on the next tick and a silo restart
/// costs nothing but latency.</para>
/// </remarks>
public class SpaceDeletionGrain(
    [PersistentState("space-deletion-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<SpaceDeletionGrainState> state,
    IDbContextFactory<ApplicationDbContext> context,
    IGrainFactory grainFactory,
    ILogger<SpaceDeletionGrain> logger) : Grain, ISpaceDeletionGrain
{
    /// <summary>A private space: long enough to sleep on, short enough not to feel like a refusal.</summary>
    private static readonly TimeSpan PrivateGrace = TimeSpan.FromDays(7);

    /// <summary>
    /// A community. Longer because the people affected mostly did not ask for this and need time to
    /// find each other somewhere else.
    /// </summary>
    private static readonly TimeSpan CommunityGrace = TimeSpan.FromDays(30);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private IDisposable? _timer;

    private Guid SpaceId => this.GetPrimaryKey();

    public override Task OnActivateAsync(CancellationToken ct)
    {
        if (state.State.Status is SpaceDeletionStatus.SCHEDULED)
            _timer = this.RegisterGrainTimer(
                static async (grain, _) => await grain.CheckAndExecuteAsync(),
                this,
                CheckInterval, CheckInterval);

        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    public async Task<(SpaceDeletionError error, SpaceDeletionState state)> RequestAsync(Guid callerId)
    {
        if (state.State.Status is SpaceDeletionStatus.EXECUTING)
            return (SpaceDeletionError.ALREADY_EXECUTING, Snapshot(false));

        if (state.State.Status is SpaceDeletionStatus.SCHEDULED)
            // Not an error worth hiding the deadline behind — the caller still wants to know when.
            return (SpaceDeletionError.ALREADY_SCHEDULED, await SnapshotAsync());

        await using var ctx = await context.CreateDbContextAsync();

        var space = await ctx.Spaces
           .AsNoTracking()
           .FirstOrDefaultAsync(x => x.Id == SpaceId);

        if (space is null)
            return (SpaceDeletionError.INTERNAL_ERROR, Snapshot(false));

        // Owner only, and deliberately not an entitlement: ManageServer is a job you can be given,
        // and destroying the place is not part of that job.
        if (space.CreatorId != callerId)
            return (SpaceDeletionError.NOT_OWNER, Snapshot(space.IsCommunity));

        var now = DateTimeOffset.UtcNow;

        state.State.Status      = SpaceDeletionStatus.SCHEDULED;
        state.State.ScheduledAt = now;
        state.State.ExecutionAt = now + (space.IsCommunity ? CommunityGrace : PrivateGrace);
        state.State.RequestedBy = callerId;
        state.State.FailureReason = null;

        await state.WriteStateAsync();

        _timer?.Dispose();
        _timer = this.RegisterGrainTimer(
            static async (grain, _) => await grain.CheckAndExecuteAsync(),
            this,
            CheckInterval, CheckInterval);

        var snapshot = Snapshot(space.IsCommunity);

        logger.LogInformation("Space {SpaceId} deletion scheduled by {CallerId} for {ExecutionAt}",
            SpaceId, callerId, state.State.ExecutionAt);

        await grainFactory.GetGrain<ISpaceGrain>(SpaceId)
           .AnnounceDeletionScheduled(snapshot);

        return (SpaceDeletionError.NONE, snapshot);
    }

    public async Task<SpaceDeletionError> CancelAsync(Guid callerId)
    {
        if (state.State.Status is SpaceDeletionStatus.EXECUTING)
            return SpaceDeletionError.ALREADY_EXECUTING;

        if (state.State.Status is not SpaceDeletionStatus.SCHEDULED)
            return SpaceDeletionError.NOT_SCHEDULED;

        await using var ctx = await context.CreateDbContextAsync();

        var space = await ctx.Spaces.AsNoTracking().FirstOrDefaultAsync(x => x.Id == SpaceId);

        if (space is null)
            return SpaceDeletionError.INTERNAL_ERROR;

        if (space.CreatorId != callerId)
            return SpaceDeletionError.NOT_OWNER;

        state.State.Status      = SpaceDeletionStatus.NONE;
        state.State.ScheduledAt = null;
        state.State.ExecutionAt = null;
        state.State.RequestedBy = null;

        await state.WriteStateAsync();

        _timer?.Dispose();
        _timer = null;

        await grainFactory.GetGrain<ISpaceGrain>(SpaceId).AnnounceDeletionCancelled();

        return SpaceDeletionError.NONE;
    }

    public async Task<SpaceDeletionState> GetStateAsync() => await SnapshotAsync();

    public async Task CheckAndExecuteAsync()
    {
        if (state.State.Status is not SpaceDeletionStatus.SCHEDULED)
            return;

        if (state.State.ExecutionAt is not { } due || due > DateTimeOffset.UtcNow)
            return;

        state.State.Status = SpaceDeletionStatus.EXECUTING;
        await state.WriteStateAsync();

        try
        {
            await grainFactory.GetGrain<ISpaceGrain>(SpaceId).DeleteSpace();

            state.State.Status = SpaceDeletionStatus.NONE;
            state.State.ScheduledAt = null;
            state.State.ExecutionAt = null;
            await state.WriteStateAsync();

            _timer?.Dispose();
            _timer = null;

            logger.LogInformation("Space {SpaceId} deleted", SpaceId);
        }
        catch (Exception e)
        {
            // Back to SCHEDULED rather than a dead-end failed state: the deadline has passed, so
            // the next tick retries. A space stuck half-deleted is worse than one deleted late.
            state.State.Status        = SpaceDeletionStatus.SCHEDULED;
            state.State.FailureReason = e.Message;
            await state.WriteStateAsync();

            logger.LogError(e, "Failed to delete space {SpaceId}; will retry", SpaceId);
        }
    }

    private SpaceDeletionState Snapshot(bool isCommunity)
        => new(state.State.Status, state.State.ScheduledAt, state.State.ExecutionAt, isCommunity);

    private async Task<SpaceDeletionState> SnapshotAsync()
    {
        await using var ctx = await context.CreateDbContextAsync();
        var isCommunity = await ctx.Spaces
           .AsNoTracking()
           .Where(x => x.Id == SpaceId)
           .Select(x => x.IsCommunity)
           .FirstOrDefaultAsync();

        return Snapshot(isCommunity);
    }
}
