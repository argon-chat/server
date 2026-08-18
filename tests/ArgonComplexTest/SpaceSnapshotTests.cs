namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The versioned bootstrap: a client that already has the space gets told so instead of being sent
/// the space again.
/// </summary>
/// <remarks>
/// The desktop client keeps every part of this in IndexedDB and asks again on every sign-in, so the
/// usual honest answer is "nothing moved". Sending the roster anyway is what made a hundred and
/// fifty simultaneous arrivals cost a second: the server was re-encoding the same members once per
/// arriving member.
/// <para>
/// What is worth pinning is both directions. Handing back a matching token has to suppress the part,
/// and a real change has to stop matching — a version that never changes and a version that always
/// changes both look fine on a single call and are each useless in one of the two ways.
/// </para>
/// </remarks>
[TestFixture]
public class SpaceSnapshotTests : TestBase
{
    private IServerInteraction Spaces(IServiceProvider provider)
        => IonClient.ForService<IServerInteraction>(provider);

    private IChannelInteraction Channels(IServiceProvider provider)
        => IonClient.ForService<IChannelInteraction>(provider);

    [Test, CancelAfter(120_000)]
    public async Task AFirstSnapshotCarriesEveryPart(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        var snapshot = await Spaces(scope.ServiceProvider).GetSpaceSnapshot(spaceId, null, ct);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.members, Is.Not.Null, "a caller with no versions gets everything");
            Assert.That(snapshot.channels, Is.Not.Null);
            Assert.That(snapshot.groups, Is.Not.Null);
            Assert.That(snapshot.archetypes, Is.Not.Null);

            Assert.That(snapshot.versions.members, Is.Not.Null.And.Not.Empty);
            Assert.That(snapshot.versions.channels, Is.Not.Null.And.Not.Empty);
            Assert.That(snapshot.versions.groups, Is.Not.Null.And.Not.Empty);
            Assert.That(snapshot.versions.archetypes, Is.Not.Null.And.Not.Empty);

            Assert.That(snapshot.members!.Value.ToList(), Has.Count.EqualTo(1), "the owner");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task HandingBackTheVersionsSuppressesEveryPart(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        var spaces = Spaces(scope.ServiceProvider);
        var first  = await spaces.GetSpaceSnapshot(spaceId, null, ct);
        var second = await spaces.GetSpaceSnapshot(spaceId, first.versions, ct);

        Assert.Multiple(() =>
        {
            Assert.That(second.members, Is.Null, "the caller said it already had this roster");
            Assert.That(second.channels, Is.Null);
            Assert.That(second.groups, Is.Null);
            Assert.That(second.archetypes, Is.Null);

            // The tokens still come back, so a client that skipped everything still has something to
            // send next time.
            Assert.That(second.versions, Is.EqualTo(first.versions));
        });
    }

    /// <summary>
    /// A version that never changes would pass the test above and quietly serve stale data forever,
    /// so the change has to be shown to break the match.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task CreatingAChannelChangesTheChannelVersionAndNothingElse(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        var spaces = Spaces(scope.ServiceProvider);
        var first  = await spaces.GetSpaceSnapshot(spaceId, null, ct);

        await Channels(scope.ServiceProvider).CreateChannel(spaceId, Guid.NewGuid(),
            new CreateChannelRequest(spaceId, "snapshot-probe", ChannelType.Text, "", null), ct);

        var second = await spaces.GetSpaceSnapshot(spaceId, first.versions, ct);

        Assert.Multiple(() =>
        {
            Assert.That(second.versions.channels, Is.Not.EqualTo(first.versions.channels));
            Assert.That(second.channels, Is.Not.Null, "the new channel has to be sent");

            Assert.That(second.versions.members, Is.EqualTo(first.versions.members),
                "nobody joined, so the roster must not be re-sent");
            Assert.That(second.members, Is.Null);
        });
    }

    /// <summary>
    /// Presence is the reason it is not in the snapshot at all: it moves on its own schedule, and a
    /// snapshot that carried it would never match twice.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task PresenceComesBackForEveryMember(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        var spaces   = Spaces(scope.ServiceProvider);
        var snapshot = await spaces.GetSpaceSnapshot(spaceId, null, ct);
        var presence = (await spaces.GetMemberPresence(spaceId, ct)).ToList();

        Assert.That(presence.Select(p => p.userId),
            Is.EquivalentTo(snapshot.members!.Value.ToList().Select(m => m.userId)));
    }
}
