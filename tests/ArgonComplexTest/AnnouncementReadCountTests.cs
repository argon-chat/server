namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using ArgonContracts;
using ion.runtime;
using static ChannelTestKit;

/// <summary>
/// How many members have read an announcement: the author and moderators may ask, readers are
/// current members whose read mark is at or past the message, and the author is not one of them.
/// </summary>
[TestFixture]
public class AnnouncementReadCountTests : TestBase
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private static IChannelInsightsInteraction Insights(TestUserSession session)
        => session.Client.ForService<IChannelInsightsInteraction>(Services);

    private Task<long> PostAsync(TestUserSession author, Guid spaceId, Guid channelId, string text, CancellationToken ct)
        => author.Channels.SendMessage(spaceId, channelId, text, new IonArray<IMessageEntity>([]), NextRandomId(), null, ct).Ok();

    private static async Task<SuccessReadCount> CountAsync(TestUserSession caller, Guid spaceId, Guid channelId, long messageId,
        Func<SuccessReadCount, bool> accept, CancellationToken ct)
    {
        var result = await PollAsync(() => Insights(caller).GetReadCount(spaceId, channelId, messageId, ct),
            r => r is SuccessReadCount s && accept(s), Window, ct);

        Assert.That(result, Is.InstanceOf<SuccessReadCount>(), $"refused: {(result as FailedReadCount)?.error}");
        return (SuccessReadCount)result;
    }

    [Test, CancelAfter(120_000)]
    public async Task The_author_sees_how_many_members_have_read_up_to_the_post(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var early  = await CreateSessionAsync(ct);
        var late   = await CreateSessionAsync(ct);
        var absent = await CreateSessionAsync(ct);
        var gone   = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        foreach (var member in new[] { early, late, absent, gone })
            await JoinAsync(owner, member, spaceId, ct);

        var first  = await PostAsync(owner, spaceId, channelId, "first", ct);
        var second = await PostAsync(owner, spaceId, channelId, "second", ct);

        // early read everything, late only the first post, absent nothing; gone read it all and left.
        await owner.Users.AckChannel(channelId, second, ct);
        await early.Users.AckChannel(channelId, second, ct);
        await late.Users.AckChannel(channelId, first, ct);
        await gone.Users.AckChannel(channelId, second, ct);
        await Grains.GetGrain<ISpaceGrain>(spaceId).RemoveMemberAsync(gone.UserId);

        var ofFirst  = await CountAsync(owner, spaceId, channelId, first, c => c.readers == 2, ct);
        var ofSecond = await CountAsync(owner, spaceId, channelId, second, c => c.readers == 1, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ofFirst.readers, Is.EqualTo(2), "early and late read the first post");
            Assert.That(ofSecond.readers, Is.EqualTo(1), "only early read the second post; the author does not count");
            Assert.That(ofFirst.members, Is.EqualTo(4), "the owner and the three who stayed");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Only_the_author_and_members_with_manage_messages_may_ask(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var herald = await CreateSessionAsync(ct);
        var reader = await CreateSessionAsync(ct);
        var mod    = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        foreach (var member in new[] { herald, reader, mod })
            await JoinAsync(owner, member, spaceId, ct);

        var archetypes = ArchetypesOf(owner);

        var heralds = await archetypes.CreateArchetype(spaceId, "herald", ct).Ok();
        await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, herald.UserId, ct), heralds.id, true, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, heralds.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.SendMessages, ct);

        var mods = await archetypes.CreateArchetype(spaceId, "mods", ct).Ok();
        await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, mod.UserId, ct), mods.id, true, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, mods.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.ManageMessages, ct);

        var post = await PollAsync(async () =>
        {
            try
            {
                return await PostAsync(herald, spaceId, channelId, "patch notes", ct);
            }
            catch
            {
                return 0L;
            }
        }, id => id != 0, Window, ct);
        Assert.That(post, Is.Not.Zero, "the herald could not post");

        await reader.Users.AckChannel(channelId, post, ct);

        var byReader = await Insights(reader).GetReadCount(spaceId, channelId, post, ct);
        var byHerald = await CountAsync(herald, spaceId, channelId, post, c => c.readers == 1, ct);
        var byMod    = await CountAsync(mod, spaceId, channelId, post, c => c.readers == 1, ct);
        var byOwner  = await CountAsync(owner, spaceId, channelId, post, c => c.readers == 1, ct);
        var unknown  = await Insights(owner).GetReadCount(spaceId, channelId, post + 1_000_000, ct);

        Assert.Multiple(() =>
        {
            Assert.That((byReader as FailedReadCount)?.error, Is.EqualTo(ReadCountError.INSUFFICIENT_PERMISSIONS),
                "a plain reader saw the count of someone else's post");
            Assert.That(byHerald.readers, Is.EqualTo(1));
            Assert.That(byMod.readers, Is.EqualTo(1));
            Assert.That(byOwner.members, Is.EqualTo(4));
            Assert.That((unknown as FailedReadCount)?.error, Is.EqualTo(ReadCountError.MESSAGE_NOT_FOUND));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_text_channel_has_no_read_count(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);

        var post   = await PostAsync(owner, spaceId, channelId, "hello", ct);
        var result = await Insights(owner).GetReadCount(spaceId, channelId, post, ct);

        Assert.That((result as FailedReadCount)?.error, Is.EqualTo(ReadCountError.NOT_AN_ANNOUNCEMENT_CHANNEL));
    }
}
