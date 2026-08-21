namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Deleting a space, which is deliberately not something that happens when you ask.
/// </summary>
/// <remarks>
/// <para>A space is other people's history as much as its owner's, so the request only writes down
/// when the deletion will happen and tells everyone in it. Until that moment the space works
/// normally and the owner can call it off. A community waits longer than a private space, because
/// more people have to find somewhere else to go.</para>
///
/// <para>What is worth pinning is therefore mostly what <em>does not</em> happen: the space is still
/// there after the call, someone who does not own it cannot start the clock, and cancelling really
/// does clear it rather than leaving a deadline nobody can see.</para>
/// </remarks>
[TestFixture]
public class SpaceDeletionTests : TestBase
{
    private IServerInteraction Spaces(IServiceProvider provider)
        => IonClient.ForService<IServerInteraction>(provider);

    private async Task<Guid> NewSpaceAsync(CancellationToken ct)
    {
        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        return await CreateSpaceAndGetIdAsync(ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task GetSpaceDeletionState_ForAFreshSpace_IsNone(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var state = await Spaces(scope.ServiceProvider).GetSpaceDeletionState(spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(state.status, Is.EqualTo(SpaceDeletionStatus.NONE));
            Assert.That(state.executionAt, Is.Null);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDeleteSpace_SchedulesRatherThanDeletes(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var result = await Spaces(scope.ServiceProvider).RequestDeleteSpace(spaceId, ct);
        Assert.That(result, Is.InstanceOf<SuccessRequestDeleteSpace>());

        var state = ((SuccessRequestDeleteSpace)result).state;

        Assert.Multiple(() =>
        {
            Assert.That(state.status, Is.EqualTo(SpaceDeletionStatus.SCHEDULED));
            Assert.That(state.executionAt, Is.Not.Null);
            // The whole point of the feature: a future date, not a tombstone.
            Assert.That(state.executionAt, Is.GreaterThan(DateTimeOffset.UtcNow));
        });

        // And the space is still a working space in the meantime.
        var stats = await Spaces(scope.ServiceProvider).GetSpaceStats(spaceId, ct);
        Assert.That(stats, Is.Not.Null);
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDeleteSpace_SurvivesAReadBack(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var scheduled = (SuccessRequestDeleteSpace)await Spaces(scope.ServiceProvider).RequestDeleteSpace(spaceId, ct);

        // The returned state is easy to get right by accident; the next read is what the header
        // shows every member.
        var reread = await Spaces(scope.ServiceProvider).GetSpaceDeletionState(spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(reread.status, Is.EqualTo(SpaceDeletionStatus.SCHEDULED));
            Assert.That(reread.executionAt, Is.EqualTo(scheduled.state.executionAt));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDeleteSpace_FromANonOwner_IsRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        // Somebody else entirely. Destroying the place is not a permission you can be handed.
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var result = await Spaces(scope.ServiceProvider).RequestDeleteSpace(spaceId, ct);

        Assert.That(result, Is.InstanceOf<FailedRequestDeleteSpace>());
        Assert.That(((FailedRequestDeleteSpace)result).error, Is.EqualTo(SpaceDeletionError.NOT_OWNER));
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDeleteSpace_Twice_StillReportsTheDeadline(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var first  = (SuccessRequestDeleteSpace)await Spaces(scope.ServiceProvider).RequestDeleteSpace(spaceId, ct);
        var second = await Spaces(scope.ServiceProvider).RequestDeleteSpace(spaceId, ct);

        Assert.That(second, Is.InstanceOf<FailedRequestDeleteSpace>());
        Assert.That(((FailedRequestDeleteSpace)second).error, Is.EqualTo(SpaceDeletionError.ALREADY_SCHEDULED));

        // Asking twice must not push the deadline out — otherwise a client that retries on a flaky
        // connection would quietly extend it forever.
        var state = await Spaces(scope.ServiceProvider).GetSpaceDeletionState(spaceId, ct);
        Assert.That(state.executionAt, Is.EqualTo(first.state.executionAt));
    }

    [Test, CancelAfter(120_000)]
    public async Task CancelDeleteSpace_ClearsTheSchedule(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        await Spaces(scope.ServiceProvider).RequestDeleteSpace(spaceId, ct);

        var cancelled = await Spaces(scope.ServiceProvider).CancelDeleteSpace(spaceId, ct);
        Assert.That(cancelled, Is.InstanceOf<SuccessCancelDeleteSpace>());

        var state = await Spaces(scope.ServiceProvider).GetSpaceDeletionState(spaceId, ct);

        Assert.Multiple(() =>
        {
            // Fully cleared, not merely "not executing": a leftover date would keep counting down
            // in every member's header.
            Assert.That(state.status, Is.EqualTo(SpaceDeletionStatus.NONE));
            Assert.That(state.executionAt, Is.Null);
            Assert.That(state.scheduledAt, Is.Null);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task CancelDeleteSpace_WhenNothingIsScheduled_SaysSo(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var result = await Spaces(scope.ServiceProvider).CancelDeleteSpace(spaceId, ct);

        Assert.That(result, Is.InstanceOf<FailedCancelDeleteSpace>());
        Assert.That(((FailedCancelDeleteSpace)result).error, Is.EqualTo(SpaceDeletionError.NOT_SCHEDULED));
    }

    [Test, CancelAfter(120_000)]
    public async Task CancelDeleteSpace_FromANonOwner_IsRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        await Spaces(scope.ServiceProvider).RequestDeleteSpace(spaceId, ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var result = await Spaces(scope.ServiceProvider).CancelDeleteSpace(spaceId, ct);

        Assert.That(result, Is.InstanceOf<FailedCancelDeleteSpace>());
        Assert.That(((FailedCancelDeleteSpace)result).error, Is.EqualTo(SpaceDeletionError.NOT_OWNER));
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDeleteSpace_AfterCancelling_CanStartAgain(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        await Spaces(scope.ServiceProvider).RequestDeleteSpace(spaceId, ct);
        await Spaces(scope.ServiceProvider).CancelDeleteSpace(spaceId, ct);

        // Cancelling is a change of mind, not a one-time escape hatch.
        var again = await Spaces(scope.ServiceProvider).RequestDeleteSpace(spaceId, ct);

        Assert.That(again, Is.InstanceOf<SuccessRequestDeleteSpace>());
        Assert.That(((SuccessRequestDeleteSpace)again).state.status, Is.EqualTo(SpaceDeletionStatus.SCHEDULED));
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDeleteSpace_ReportsWhetherTheWaitIsACommunityOne(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var state = ((SuccessRequestDeleteSpace)await Spaces(scope.ServiceProvider).RequestDeleteSpace(spaceId, ct)).state;

        // A fresh space is private, so it gets the short wait. The flag travels with the state so
        // the client can explain the length rather than showing a bare date.
        Assert.That(state.isCommunity, Is.False);
        Assert.That(state.executionAt, Is.LessThan(DateTimeOffset.UtcNow.AddDays(8)));
        Assert.That(state.executionAt, Is.GreaterThan(DateTimeOffset.UtcNow.AddDays(6)));
    }
}
