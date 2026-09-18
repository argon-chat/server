namespace ArgonComplexTest.Tests;

using ArgonContracts;
using ion.runtime;

/// <summary>
/// Asking about a whole member list at once.
/// </summary>
/// <remarks>
/// <para><c>PrefetchProfile</c> answers about one member per call, and a member list wants one per
/// row — so opening a space with a few hundred members cost a few hundred round trips, each with a
/// DbContext and a join over three tables behind it. <c>PrefetchProfiles</c> is the same answer for
/// many members in one query; the single-member call stays for the single-member case.</para>
///
/// <para>The batch is also the first of the pair to ask whether the caller belongs in the space it
/// names. That matters more here than it did there: a call that answers about a hundred ids is a
/// directory walk if nothing stands behind the space id, so the refusal is the test that earns its
/// place — the successes only show the feature works.</para>
/// </remarks>
[TestFixture]
public class ProfilePrefetchTests : TestBase
{
    private static async Task<Guid> NewSpaceAsync(TestUserSession owner, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(
            new CreateServerRequest("Prefetch Space", "Description", string.Empty), ct);

        Assert.That(result, Is.InstanceOf<SuccessCreateSpace>(),
            $"Could not create the space: {(result as FailedCreateSpace)?.error}");

        return ((SuccessCreateSpace)result).space.spaceId;
    }

    private static async Task JoinAsync(TestUserSession owner, TestUserSession member, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await member.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"Member could not join: {(joined as FailedJoin)?.error}");
    }

    [Test, CancelAfter(120_000)]
    public async Task PrefetchProfiles_AnswersOneEntryPerIdInTheOrderAsked(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId = await NewSpaceAsync(owner, ct);
        await JoinAsync(owner, member, spaceId, ct);

        // Asked member-first so a right answer cannot be the request order coming back by accident.
        var asked    = new IonArray<Guid>([member.UserId, owner.UserId]);
        var profiles = await owner.Servers.PrefetchProfiles(spaceId, asked, ct);

        Assert.That(profiles.Values.Select(p => p.userId),
            Is.EqualTo(new[] { member.UserId, owner.UserId }));
    }

    [Test, CancelAfter(120_000)]
    public async Task PrefetchProfiles_SaysWhatPrefetchProfileSays(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId = await NewSpaceAsync(owner, ct);
        await JoinAsync(owner, member, spaceId, ct);

        var one   = await owner.Servers.PrefetchProfile(spaceId, member.UserId, ct);
        var batch = await owner.Servers.PrefetchProfiles(spaceId, new IonArray<Guid>([member.UserId]), ct);

        Assert.That(batch.Values, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(batch.Values[0].userId, Is.EqualTo(one.userId));
            Assert.That(batch.Values[0].bio, Is.EqualTo(one.bio));
            Assert.That(batch.Values[0].customStatus, Is.EqualTo(one.customStatus));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task PrefetchProfiles_FromSomeoneOutsideTheSpace_IsRefused(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var member   = await CreateSessionAsync(ct);
        var outsider = await CreateSessionAsync(ct);

        var spaceId = await NewSpaceAsync(owner, ct);
        await JoinAsync(owner, member, spaceId, ct);

        // The space id is the only thing saying the caller has met these people. Naming one they are
        // not in has to be refused outright, or a bare user id is all a directory walk needs.
        Assert.ThrowsAsync<InvalidOperationException>(
            () => outsider.Servers.PrefetchProfiles(spaceId, new IonArray<Guid>([member.UserId]), ct));
    }

    [Test, CancelAfter(120_000)]
    public async Task PrefetchProfiles_ForAStrangerId_AnswersAPlaceholderRatherThanAProfile(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await NewSpaceAsync(owner, ct);

        var stranger = Guid.NewGuid();
        var profiles = await owner.Servers.PrefetchProfiles(spaceId, new IonArray<Guid>([stranger]), ct);

        Assert.That(profiles.Values, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(profiles.Values[0].userId, Is.EqualTo(stranger));
            Assert.That(profiles.Values[0].bio, Is.EqualTo("Deleted Account"));
            // An id that belongs to nobody in this space must not come back carrying roles.
            Assert.That(profiles.Values[0].archetypes.Values, Is.Empty);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task PrefetchProfiles_PastTheCap_DropsTheOverflowRatherThanAnsweringIt(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await NewSpaceAsync(owner, ct);

        var tooMany  = new IonArray<Guid>(Enumerable.Range(0, 150).Select(_ => Guid.NewGuid()).ToList());
        var profiles = await owner.Servers.PrefetchProfiles(spaceId, tooMany, ct);

        // The cap is what keeps one call from being a walk over every account the caller can name.
        Assert.That(profiles.Values, Has.Count.EqualTo(100));
    }
}
