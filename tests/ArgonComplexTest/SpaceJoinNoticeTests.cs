namespace ArgonComplexTest.Tests;

using Argon.Entities;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The "… joined the space" notice that a non-community space drops into its default channel.
/// </summary>
/// <remarks>
/// <para>Guards the id the notice is written with. <c>ArgonMessageEntity.MessageId</c> is
/// <c>ValueGeneratedNever</c> and forms the last third of the <c>(SpaceId, ChannelId, MessageId)</c>
/// primary key, so a notice built without an id was inserted as 0: the first join into a space took
/// that row and every join after it died on the duplicate key. Nothing surfaced, because
/// <c>SpaceGrain</c> writes the notice detached — which is why the regression needs a test that
/// counts the notices rather than trusting the join call to have thrown.</para>
///
/// <para>Two joins, not one, is the whole point. One join passes against the broken code, id 0 and
/// all, and passed for as long as the bug existed.</para>
/// </remarks>
[TestFixture]
public class SpaceJoinNoticeTests : TestBase
{
    private const string JoinedMarker = "joined the space";

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task Two_users_joining_one_space_each_get_a_notice_with_its_own_non_zero_id(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var first  = await CreateSessionAsync(ct);
        var second = await CreateSessionAsync(ct);

        var firstName  = (await first.Users.GetMe(ct)).username;
        var secondName = (await second.Users.GetMe(ct)).username;

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "join-notices", ct);

        // Nothing in the product assigns DefaultChannelId yet, so a space created through the API has
        // none and SendUserJoinedMessageAsync returns early. Setting it by hand is what puts the code
        // under test on the path at all; the space stays non-community, which is the other condition.
        await SetDefaultChannelAsync(spaceId, channelId, ct);

        await JoinAsync(owner, first, spaceId, ct);
        await JoinAsync(owner, second, spaceId, ct);

        var notices = await WaitForJoinNoticesAsync(owner, spaceId, channelId, 2, ct);
        var ids     = notices.Select(m => m.messageId).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(notices, Has.Count.EqualTo(2),
                "both joins should have left a notice — a missing second one means the insert collided on the primary key");
            Assert.That(ids, Has.None.EqualTo(0L),
                "id 0 means nobody minted one and the column default did not fire either");
            Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count),
                "the two notices share an id, so only one of them can exist in the channel");
            Assert.That(notices.Select(m => m.sender), Is.All.EqualTo(UserEntity.SystemUser));
            Assert.That(notices.Any(m => m.text.Contains(firstName)), Is.True,
                "no notice names the first user who joined");
            Assert.That(notices.Any(m => m.text.Contains(secondName)), Is.True,
                "no notice names the second user who joined");
        });
    }

    /// <summary>
    /// Reads the channel until <paramref name="expected"/> notices are there, or gives up and returns
    /// what it did find so the assertions can say how far short it fell.
    /// </summary>
    /// <remarks>
    /// The notice is written detached from the join call — <c>SpaceGrain</c> returns before the insert
    /// happens — so no API answer promises it has landed. Polling rather than a fixed sleep: the wait
    /// is normally one round trip, and the ceiling only ever gets spent on a loaded runner.
    /// </remarks>
    private static async Task<List<ArgonMessage>> WaitForJoinNoticesAsync(
        TestUserSession reader, Guid spaceId, Guid channelId, int expected, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (true)
        {
            var messages = await reader.Channels.QueryMessages(spaceId, channelId, null, 50, ct);
            var found    = messages.Values.Where(m => m.text.Contains(JoinedMarker)).ToList();

            if (found.Count >= expected || DateTimeOffset.UtcNow >= deadline)
                return found;

            await Task.Delay(250, ct);
        }
    }

    private async Task<Guid> CreateSpaceAsync(TestUserSession owner, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest("Join Notice Space", "Description", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)!.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    private async Task<Guid> CreateChannelAsync(TestUserSession owner, Guid spaceId, string name, CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, ChannelType.Text, "Where join notices land", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var created  = channels.Values.FirstOrDefault(c => c.channel.name == name);

        if (created is null)
        {
            Assert.Fail($"Failed to find created channel '{name}'");
            return Guid.Empty;
        }

        return created.channel.channelId;
    }

    private async Task JoinAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"Guest could not join: {(joined as FailedJoin)?.error}");
    }

    private async Task SetDefaultChannelAsync(Guid spaceId, Guid channelId, CancellationToken ct)
    {
        var factory = FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        await using var db = await factory.CreateDbContextAsync(ct);

        var space = await db.Spaces.FirstAsync(s => s.Id == spaceId, ct);

        space.DefaultChannelId = channelId;

        await db.SaveChangesAsync(ct);
    }
}
