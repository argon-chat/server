namespace ArgonComplexTest.Tests;

using ArgonContracts;
using ion.runtime;
using static ChannelTestKit;

/// <summary>
/// Reactions on channel messages: who may add one, the limits, the refusals, and the buffer the
/// channel grain keeps them in between flushes.
/// </summary>
/// <remarks>
/// The grain answers reactions from memory and writes them back on a three-second timer, with a
/// hundred-message LRU in front of the database. Two things about that are worth pinning: a reaction
/// not yet flushed must never be the entry that gets evicted, and an evicted entry must come back from
/// the database exactly as it was.
/// </remarks>
[TestFixture]
public class ChannelReactionTests : TestBase
{
    private static IonArray<IMessageEntity> NoEntities => new([]);

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private async Task<(TestUserSession Owner, Guid SpaceId, Guid ChannelId)> RoomAsync(CancellationToken ct)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "reactions", ChannelType.Text, ct);
        return (owner, spaceId, channelId);
    }

    private static async Task<List<ReactionInfo>> ReactionsOnAsync(TestUserSession session, Guid spaceId, Guid channelId, long messageId,
        CancellationToken ct)
    {
        var entries = await session.Channels.BatchGetReactions(spaceId, channelId, new IonArray<long>([messageId]), ct);
        return entries.Values.FirstOrDefault(e => e.messageId == messageId)?.reactions.Values.ToList() ?? [];
    }

    [Test, CancelAfter(120_000)]
    public async Task A_second_member_joins_an_existing_reaction_and_both_are_listed(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var guest = await CreateSessionAsync(ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "react to me", NoEntities, NextRandomId(), null, ct).Ok();

        Assert.That(await owner.Channels.AddReaction(spaceId, channelId, messageId, "👍", ct), Is.InstanceOf<SuccessAddReaction>());
        Assert.That(await guest.Channels.AddReaction(spaceId, channelId, messageId, "👍", ct), Is.InstanceOf<SuccessAddReaction>());

        var thumbs = (await ReactionsOnAsync(owner, spaceId, channelId, messageId, ct)).Single(r => r.emoji == "👍");

        Assert.Multiple(() =>
        {
            Assert.That(thumbs.count, Is.EqualTo(2), "the second member started a new reaction instead of joining the first");
            Assert.That(thumbs.userIds.Values, Is.EquivalentTo(new[] { owner.UserId, guest.UserId }));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Reacting_twice_with_the_same_emoji_is_refused_and_counted_once(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "once", NoEntities, NextRandomId(), null, ct).Ok();

        await owner.Channels.AddReaction(spaceId, channelId, messageId, "🔥", ct);
        var again = await owner.Channels.AddReaction(spaceId, channelId, messageId, "🔥", ct);

        Assert.That(again, Is.InstanceOf<FailedAddReaction>());
        Assert.That(((FailedAddReaction)again).error, Is.EqualTo(AddReactionError.ALREADY_REACTED));
        Assert.That((await ReactionsOnAsync(owner, spaceId, channelId, messageId, ct)).Single().count, Is.EqualTo(1));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_message_takes_twenty_distinct_emoji_and_no_more_but_an_existing_one_can_still_be_joined(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var guest = await CreateSessionAsync(ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "a wall of emoji", NoEntities, NextRandomId(), null, ct).Ok();

        for (var i = 0; i < 20; i++)
            Assert.That(await owner.Channels.AddReaction(spaceId, channelId, messageId, $"e{i}", ct), Is.InstanceOf<SuccessAddReaction>(),
                $"emoji {i} of twenty was refused");

        var twentyFirst = await guest.Channels.AddReaction(spaceId, channelId, messageId, "e20", ct);

        Assert.That(twentyFirst, Is.InstanceOf<FailedAddReaction>());
        Assert.That(((FailedAddReaction)twentyFirst).error, Is.EqualTo(AddReactionError.REACTION_LIMIT_REACHED));

        // The cap is on distinct emoji, not on people: joining one already there adds no new kind.
        Assert.That(await guest.Channels.AddReaction(spaceId, channelId, messageId, "e0", ct), Is.InstanceOf<SuccessAddReaction>());

        var reactions = await ReactionsOnAsync(owner, spaceId, channelId, messageId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(reactions, Has.Count.EqualTo(20));
            Assert.That(reactions.Single(r => r.emoji == "e0").count, Is.EqualTo(2));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_reaction_to_a_message_that_is_not_in_this_channel_is_refused(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var otherChannel = await CreateChannelAsync(owner, spaceId, "elsewhere", ChannelType.Text, ct);

        var elsewhere = await owner.Channels.SendMessage(spaceId, otherChannel, "not here", NoEntities, NextRandomId(), null, ct).Ok();

        // A message id is only meaningful inside its channel: the grain for this one must not reach
        // into its neighbour's rows.
        var added   = await owner.Channels.AddReaction(spaceId, channelId, elsewhere, "👀", ct);
        var removed = await owner.Channels.RemoveReaction(spaceId, channelId, elsewhere, "👀", ct);

        Assert.Multiple(() =>
        {
            Assert.That(added, Is.InstanceOf<FailedAddReaction>());
            Assert.That((added as FailedAddReaction)?.error, Is.EqualTo(AddReactionError.MESSAGE_NOT_FOUND));
            Assert.That(removed, Is.InstanceOf<FailedRemoveReaction>());
            Assert.That((removed as FailedRemoveReaction)?.error, Is.EqualTo(RemoveReactionError.MESSAGE_NOT_FOUND));
        });

        Assert.That(await ReactionsOnAsync(owner, spaceId, otherChannel, elsewhere, ct), Is.Empty);
    }

    [Test, CancelAfter(120_000)]
    public async Task A_deleted_message_can_no_longer_be_reacted_to_or_have_reactions_read(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);

        var warm = await owner.Channels.SendMessage(spaceId, channelId, "reacted, then deleted", NoEntities, NextRandomId(), null, ct).Ok();
        var cold = await owner.Channels.SendMessage(spaceId, channelId, "deleted before anyone reacted", NoEntities, NextRandomId(), null, ct).Ok();

        // One whose reactions the grain already holds in memory, one it has never loaded: the delete
        // has to reach both the database read and the buffer.
        await owner.Channels.AddReaction(spaceId, channelId, warm, "👍", ct);

        Assert.That(await owner.Channels.DeleteMessage(spaceId, channelId, warm, ct), Is.InstanceOf<SuccessDeleteMessage>());
        Assert.That(await owner.Channels.DeleteMessage(spaceId, channelId, cold, ct), Is.InstanceOf<SuccessDeleteMessage>());

        var onWarm   = await owner.Channels.AddReaction(spaceId, channelId, warm, "🎉", ct);
        var onCold   = await owner.Channels.AddReaction(spaceId, channelId, cold, "🎉", ct);
        var offWarm  = await owner.Channels.RemoveReaction(spaceId, channelId, warm, "👍", ct);
        var batch    = await owner.Channels.BatchGetReactions(spaceId, channelId, new IonArray<long>([warm, cold]), ct);

        Assert.Multiple(() =>
        {
            Assert.That((onWarm as FailedAddReaction)?.error, Is.EqualTo(AddReactionError.MESSAGE_NOT_FOUND),
                "a message the grain had cached took a reaction after it was deleted");
            Assert.That((onCold as FailedAddReaction)?.error, Is.EqualTo(AddReactionError.MESSAGE_NOT_FOUND),
                "a deleted message took a reaction");
            Assert.That((offWarm as FailedRemoveReaction)?.error, Is.EqualTo(RemoveReactionError.MESSAGE_NOT_FOUND));
            Assert.That(batch.Values.Select(e => e.messageId), Is.Empty,
                "reactions of deleted messages are still served");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Removing_a_reaction_the_caller_never_added_is_refused_and_leaves_the_others(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var guest = await CreateSessionAsync(ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "mine", NoEntities, NextRandomId(), null, ct).Ok();
        await owner.Channels.AddReaction(spaceId, channelId, messageId, "👍", ct);

        var notTheirs = await guest.Channels.RemoveReaction(spaceId, channelId, messageId, "👍", ct);
        var notThere  = await owner.Channels.RemoveReaction(spaceId, channelId, messageId, "🎉", ct);

        Assert.Multiple(() =>
        {
            Assert.That((notTheirs as FailedRemoveReaction)?.error, Is.EqualTo(RemoveReactionError.REACTION_NOT_FOUND),
                "one member removed another member's reaction");
            Assert.That((notThere as FailedRemoveReaction)?.error, Is.EqualTo(RemoveReactionError.REACTION_NOT_FOUND));
        });

        var thumbs = (await ReactionsOnAsync(owner, spaceId, channelId, messageId, ct)).Single();
        Assert.That(thumbs.userIds.Values, Is.EqualTo(new[] { owner.UserId }));
    }

    [Test, CancelAfter(120_000)]
    public async Task Reactions_are_refused_in_a_voice_channel(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var voice   = await CreateChannelAsync(owner, spaceId, "the-room", ChannelType.Voice, ct);

        var result = await owner.Channels.AddReaction(spaceId, voice, 1, "👍", ct);

        Assert.That(result, Is.InstanceOf<FailedAddReaction>());
    }

    [Test, CancelAfter(120_000)]
    public async Task Without_AddReactions_a_member_cannot_react(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var guest = await CreateSessionAsync(ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "no reactions here", NoEntities, NextRandomId(), null, ct).Ok();

        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.AddReactions, ct);

        var refused = await guest.Channels.AddReaction(spaceId, channelId, messageId, "👍", ct);

        Assert.That((refused as FailedAddReaction)?.error, Is.EqualTo(AddReactionError.INSUFFICIENT_PERMISSIONS));
        Assert.That(await ReactionsOnAsync(owner, spaceId, channelId, messageId, ct), Is.Empty);
    }

    [Test, CancelAfter(120_000)]
    public async Task Reactions_are_not_served_to_someone_who_cannot_read_the_channel(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var guest    = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "who reacted is not public", NoEntities, NextRandomId(), null, ct).Ok();
        await owner.Channels.AddReaction(spaceId, channelId, messageId, "👍", ct);

        Assert.That(await ReactionsOnAsync(guest, spaceId, channelId, messageId, ct), Is.Not.Empty,
            "premise: a member reads the reactions before the deny");

        var fromStranger = await ReactionsOnAsync(stranger, spaceId, channelId, messageId, ct);

        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.ReadHistory, ct);
        var afterDeny = await ReactionsOnAsync(guest, spaceId, channelId, messageId, ct);

        Assert.Multiple(() =>
        {
            // The same line QueryMessages draws: a channel the caller cannot read answers as empty,
            // and who reacted with what is part of what they cannot read.
            Assert.That(fromStranger, Is.Empty, "a channel id was enough to read who reacted in a space the caller is not in");
            Assert.That(afterDeny, Is.Empty, "a member denied ReadHistory still read the reactions");
        });
    }

    /// <summary>
    /// A hundred and four messages, and a reaction on the oldest, walked through every eviction the
    /// buffer does.
    /// </summary>
    /// <remarks>
    /// While the reaction is still only in memory the entry holding it is the least recently used one,
    /// and both loaders — the batch read and the single-message load behind add/remove — must stop at
    /// it rather than drop it. Once the flush has written it, it is an ordinary cold entry and goes;
    /// the next read has to bring it back from the row.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task An_unflushed_reaction_survives_the_buffer_filling_up_and_an_evicted_one_comes_back_from_the_row(
        CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);

        var ids = new List<long>();
        for (var i = 0; i < 104; i++)
            ids.Add(await owner.Channels.SendMessage(spaceId, channelId, $"m{i}", NoEntities, NextRandomId(), null, ct).Ok());

        Assert.That(await owner.Channels.AddReaction(spaceId, channelId, ids[0], "👍", ct), Is.InstanceOf<SuccessAddReaction>());

        // Fifty is the batch ceiling: a request for fifty-one answers for the first fifty only.
        var capped = await owner.Channels.BatchGetReactions(spaceId, channelId, new IonArray<long>(ids.Skip(1).Take(51).ToList()), ct);
        Assert.That(capped.Values, Has.Count.EqualTo(50));

        await owner.Channels.BatchGetReactions(spaceId, channelId, new IonArray<long>(ids.Skip(51).Take(50).ToList()), ct);

        // The single-message loader, pushed past the limit while the oldest entry is still owed a write.
        var nothingToRemove = await owner.Channels.RemoveReaction(spaceId, channelId, ids[101], "👍", ct);
        Assert.That((nothingToRemove as FailedRemoveReaction)?.error, Is.EqualTo(RemoveReactionError.REACTION_NOT_FOUND));

        // Not read back through the grain here: a read would move the entry to the front and the
        // eviction below would never reach it. The flush only writes what the buffer still holds, so
        // the row taking the reaction is the proof that neither loader dropped it.
        var stored = await PollAsync(
            () => StoredMessageAsync(spaceId, channelId, ids[0], ct),
            m => m?.Reactions is { Count: > 0 }, TimeSpan.FromSeconds(30), ct);

        Assert.That(stored?.Reactions?.Single().UserIds, Is.EqualTo(new List<Guid> { owner.UserId }),
            "the reaction was dropped from the buffer before the flush could write it");

        // Now cold: both loaders evict it and whatever else is oldest.
        await owner.Channels.BatchGetReactions(spaceId, channelId, new IonArray<long>([ids[102]]), ct);
        await owner.Channels.RemoveReaction(spaceId, channelId, ids[103], "👍", ct);

        var reloaded = await ReactionsOnAsync(owner, spaceId, channelId, ids[0], ct);

        Assert.That(reloaded.Single().userIds.Values, Is.EqualTo(new[] { owner.UserId }),
            "an evicted entry did not come back from the database as it was");
    }
}
