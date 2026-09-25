namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using Argon.Grains.Persistence.States;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Storage;
using static SpaceGroupSupport;

/// <summary>
/// The part of space deletion that happens when nobody is looking: the timer's check, and a schedule
/// picked up by an activation that did not write it.
/// </summary>
/// <remarks>
/// <para><c>SpaceDeletionTests</c> pins the request and the cancellation — what an owner sees. This
/// fixture is about the grain's own state across activations, which is where a silo restart puts
/// it: the state is read back from storage and whatever it says has to be carried on with. Several
/// tests therefore write that state straight into the grain's store before its first activation,
/// exactly as a silo that stopped at that moment would have left it.</para>
/// </remarks>
[TestFixture]
public class SpaceDeletionRecoveryTests : TestBase
{
    private const string StateName = "space-deletion-store";

    private static IGrainStorage Store()
        => ArgonTestEnvironment.Instance.Host.Services
           .GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);

    private static ISpaceDeletionGrain Deletion(Guid spaceId) => Grains.GetGrain<ISpaceDeletionGrain>(spaceId);

    /// <summary>Writes the grain's persisted state before anything has activated it.</summary>
    private static async Task SeedAsync(Guid spaceId, SpaceDeletionGrainState seed)
    {
        var state = new GrainState<SpaceDeletionGrainState>(new SpaceDeletionGrainState());
        var id    = Deletion(spaceId).GetGrainId();

        await Store().ReadStateAsync(StateName, id, state);
        state.State = seed;
        await Store().WriteStateAsync(StateName, id, state);
    }

    private static async Task<bool> IsLiveAsync(Guid spaceId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        return await db.Spaces.AnyAsync(s => s.Id == spaceId, ct);
    }

    private static SpaceDeletionGrainState Overdue(SpaceDeletionStatus status, Guid requestedBy) => new()
    {
        Status      = status,
        ScheduledAt = DateTimeOffset.UtcNow.AddDays(-8),
        ExecutionAt = DateTimeOffset.UtcNow.AddDays(-1),
        RequestedBy = requestedBy
    };

    [Test, CancelAfter(120_000)]
    public async Task RequestDeleteSpace_ForASpaceThatDoesNotExist_IsRefused(CancellationToken ct = default)
    {
        var caller = await CreateSessionAsync(ct);
        var nobody = Guid.NewGuid();

        var result = await caller.Servers.RequestDeleteSpace(nobody, ct);
        var state  = await caller.Servers.GetSpaceDeletionState(nobody, ct);

        Assert.Multiple(() =>
        {
            Assert.That((result as FailedRequestDeleteSpace)?.error, Is.EqualTo(SpaceDeletionError.INTERNAL_ERROR));
            Assert.That(state.status, Is.EqualTo(SpaceDeletionStatus.NONE), "a schedule was written for a space that is not there");
        });
    }

    /// <summary>The timer's check is safe to run at any moment: before the deadline it touches nothing.</summary>
    [Test, CancelAfter(120_000)]
    public async Task CheckAndExecute_BeforeTheDeadline_LeavesTheSpaceAlone(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Not yet", ct);

        await Deletion(spaceId).CheckAndExecuteAsync();
        var unscheduled = await IsLiveAsync(spaceId, ct);

        var requested = await owner.Servers.RequestDeleteSpace(spaceId, ct);
        Assert.That(requested, Is.InstanceOf<SuccessRequestDeleteSpace>());

        await Deletion(spaceId).CheckAndExecuteAsync();

        var state = await owner.Servers.GetSpaceDeletionState(spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(unscheduled, Is.True, "a check with nothing scheduled deleted the space");
            Assert.That(state.status, Is.EqualTo(SpaceDeletionStatus.SCHEDULED));
        });
        Assert.That(await IsLiveAsync(spaceId, ct), Is.True, "the space was deleted before its deadline");
    }

    /// <summary>
    /// A schedule written by one activation is carried out by the next: the deadline lives in the
    /// state, so a silo restart costs latency and nothing else.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_schedule_left_by_an_earlier_activation_is_carried_out_by_the_next(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Inherited schedule", ct);
        var seed    = Overdue(SpaceDeletionStatus.SCHEDULED, owner.UserId);

        await SeedAsync(spaceId, seed);

        var picked = await owner.Servers.GetSpaceDeletionState(spaceId, ct);
        await Deletion(spaceId).CheckAndExecuteAsync();
        var after = await Deletion(spaceId).GetStateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(picked.status, Is.EqualTo(SpaceDeletionStatus.SCHEDULED));
            Assert.That(picked.executionAt, Is.EqualTo(seed.ExecutionAt).Within(TimeSpan.FromSeconds(1)));
            Assert.That(after.status, Is.EqualTo(SpaceDeletionStatus.NONE));
        });
        Assert.That(await IsLiveAsync(spaceId, ct), Is.False, "an overdue schedule was not carried out");
    }

    /// <summary>
    /// A deletion interrupted between "executing" and "done" is retried, not abandoned.
    /// </summary>
    /// <remarks>
    /// <c>CheckAndExecuteAsync</c> writes EXECUTING before it deletes and NONE after, and every path
    /// in the grain treats EXECUTING as "someone is on it" — a request, a cancellation, the timer's
    /// check and the account erasure's <c>DeleteNowAsync</c> all stood down. The grain is not
    /// reentrant, so the only activation that can read EXECUTING is one whose predecessor died in the
    /// middle, and nobody was on it any more: the space was never deleted, its owner could neither
    /// cancel nor re-request, and no timer was even registered to try again. The grain's own rule for
    /// a failed attempt — back to SCHEDULED, the deadline has passed, the next tick retries — is what
    /// an interrupted one needs too.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_deletion_interrupted_mid_way_is_retried_rather_than_stuck(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Interrupted", ct);

        await SeedAsync(spaceId, Overdue(SpaceDeletionStatus.EXECUTING, owner.UserId));

        var picked = await owner.Servers.GetSpaceDeletionState(spaceId, ct);
        await Deletion(spaceId).CheckAndExecuteAsync();
        var after = await Deletion(spaceId).GetStateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(picked.status, Is.EqualTo(SpaceDeletionStatus.SCHEDULED));
            Assert.That(after.status, Is.EqualTo(SpaceDeletionStatus.NONE));
        });
        Assert.That(await IsLiveAsync(spaceId, ct), Is.False, "the interrupted deletion was never finished");
    }

    /// <summary>
    /// The account erasure's immediate delete does not report success for a space it did not delete.
    /// </summary>
    /// <remarks>
    /// <c>DeleteNowAsync</c> returned without a word when it found EXECUTING, and
    /// <c>AccountDeletionGrain</c> took that as done — leaving a private space whose owner had been
    /// erased, which is the one outcome that step exists to prevent.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task DeleteNow_on_an_interrupted_deletion_still_deletes_the_space(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Interrupted, then erased", ct);

        await SeedAsync(spaceId, Overdue(SpaceDeletionStatus.EXECUTING, owner.UserId));

        await Deletion(spaceId).DeleteNowAsync(owner.UserId);

        Assert.That(await IsLiveAsync(spaceId, ct), Is.False, "DeleteNowAsync returned and the space is still there");
        Assert.That((await Deletion(spaceId).GetStateAsync()).status, Is.EqualTo(SpaceDeletionStatus.NONE));
    }

    /// <summary>
    /// A schedule that outlived its space cannot be cancelled into a state that pretends otherwise.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task CancelDeleteSpace_WhenTheSpaceIsGone_IsRefused(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var nobody = Guid.NewGuid();

        await SeedAsync(nobody, Overdue(SpaceDeletionStatus.SCHEDULED, owner.UserId) with
        {
            ExecutionAt = DateTimeOffset.UtcNow.AddDays(5)
        });

        var cancelled = await owner.Servers.CancelDeleteSpace(nobody, ct);

        Assert.That((cancelled as FailedCancelDeleteSpace)?.error, Is.EqualTo(SpaceDeletionError.INTERNAL_ERROR));
    }
}
