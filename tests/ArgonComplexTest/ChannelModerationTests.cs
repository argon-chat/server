namespace ArgonComplexTest.Tests;

using ArgonContracts;
using ion.runtime;

/// <summary>
/// Channel settings and message moderation: renaming a room, its cooldown, taking messages down, and
/// handing out a link to a voice room.
/// </summary>
/// <remarks>
/// <para>Every test here needs two identities that differ in one specific way — the space creator
/// carries the seeded "owner" archetype (<c>Administrator</c>, i.e. every bit), anyone who joins
/// through an invite carries "everyone" (chat and voice, no management bits). That is the whole
/// reason these use <see cref="TestBase.CreateSessionAsync"/> rather than the ambient token: a
/// permission test with one identity can only ever prove that the allowed path works.</para>
///
/// <para>The refusals are the half worth guarding. Slow mode that also throttles the moderator makes
/// the feature unusable by the person who turned it on, and a delete that does not check who is
/// asking turns every member into a moderator.</para>
/// </remarks>
[TestFixture]
public class ChannelModerationTests : TestBase
{
    private static IonArray<IMessageEntity> NoEntities => new([]);

    // Message ids are the client's de-duplication key, not the server's counter — reusing one inside
    // a fixture would make the second send return the first message's id instead of posting.
    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private async Task<Guid> CreateSpaceAsync(TestUserSession owner, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest("Moderated Space", "Description", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)!.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    private async Task<Guid> CreateChannelAsync(TestUserSession owner, Guid spaceId, string name, ChannelType kind, CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, kind, "Test channel", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var created  = channels.Values.FirstOrDefault(c => c.channel.name == name);

        if (created is null)
        {
            Assert.Fail($"Failed to find created channel '{name}'");
            return Guid.Empty;
        }

        return created.channel.channelId;
    }

    /// <summary>Puts <paramref name="guest"/> in the space as an ordinary member — "everyone" only.</summary>
    private async Task JoinAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"Guest could not join: {(joined as FailedJoin)?.error}");
    }

    private async Task<ArgonChannel> ReadChannelAsync(TestUserSession session, Guid spaceId, Guid channelId, CancellationToken ct)
    {
        var channels = await session.Servers.GetChannels(spaceId, ct);
        return channels.Values.First(c => c.channel.channelId == channelId).channel;
    }

    // ── UpdateChannel ───────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateChannel_RenamesAndRetopicsTheChannel(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "before", ChannelType.Text, ct);

        var result = await owner.Channels.UpdateChannel(spaceId, channelId, "after", "the new topic", null, ct);

        Assert.That(result, Is.InstanceOf<SuccessUpdateChannel>(),
            $"Owner was refused: {(result as FailedUpdateChannel)?.error}");

        // Read it back through GetChannels rather than trusting the echo: the point of the method is
        // that the change is durable, not that the response says so.
        var channel = await ReadChannelAsync(owner, spaceId, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(channel.name, Is.EqualTo("after"));
            Assert.That(channel.description, Is.EqualTo("the new topic"));
        });
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateChannel_TreatsNullAsLeaveAlone(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "keep-me", ChannelType.Text, ct);

        // Renaming from the settings sheet must not wipe the topic the user never touched.
        await owner.Channels.UpdateChannel(spaceId, channelId, null, "a topic", null, ct);
        await owner.Channels.UpdateChannel(spaceId, channelId, "renamed", null, null, ct);

        var channel = await ReadChannelAsync(owner, spaceId, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(channel.name, Is.EqualTo("renamed"));
            Assert.That(channel.description, Is.EqualTo("a topic"));
        });
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateChannel_WithoutManageChannels_IsRefused(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var guest     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "not-yours", ChannelType.Text, ct);

        await JoinAsync(owner, guest, spaceId, ct);

        var result = await guest.Channels.UpdateChannel(spaceId, channelId, "hijacked", null, null, ct);

        Assert.That(result, Is.InstanceOf<FailedUpdateChannel>());
        Assert.That(((FailedUpdateChannel)result).error, Is.EqualTo(UpdateChannelError.INSUFFICIENT_PERMISSIONS));

        // A refusal that still wrote is not a refusal.
        var channel = await ReadChannelAsync(owner, spaceId, channelId, ct);
        Assert.That(channel.name, Is.EqualTo("not-yours"));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateChannel_WithAnEmptyName_IsRefused(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "named", ChannelType.Text, ct);

        // Whitespace counts as empty: a channel whose name renders as nothing cannot be clicked.
        var result = await owner.Channels.UpdateChannel(spaceId, channelId, "   ", null, null, ct);

        Assert.That(result, Is.InstanceOf<FailedUpdateChannel>());
        Assert.That(((FailedUpdateChannel)result).error, Is.EqualTo(UpdateChannelError.NAME_EMPTY));
    }

    // ── Slow mode ───────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateChannel_AcceptsEveryCooldownTheDesignOffers(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "slow", ChannelType.Text, ct);

        // The picker has exactly six positions. If the server ever narrows this set, a client that
        // still renders six of them starts producing values the server rejects.
        foreach (var seconds in new[] { 5, 15, 30, 60, 300 })
        {
            var result = await owner.Channels.UpdateChannel(spaceId, channelId, null, null, seconds, ct);
            Assert.That(result, Is.InstanceOf<SuccessUpdateChannel>(), $"{seconds}s was refused");

            var channel = await ReadChannelAsync(owner, spaceId, channelId, ct);
            Assert.That(channel.slowModeSeconds, Is.EqualTo(seconds));
        }

        // Off is the sixth, and it travels back as null rather than 0 so the client has one way of
        // spelling "no cooldown" instead of two.
        var cleared = await owner.Channels.UpdateChannel(spaceId, channelId, null, null, 0, ct);
        Assert.That(cleared, Is.InstanceOf<SuccessUpdateChannel>());
        Assert.That((await ReadChannelAsync(owner, spaceId, channelId, ct)).slowModeSeconds, Is.Null);
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateChannel_RejectsACooldownOutsideThePicker(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "slow", ChannelType.Text, ct);

        var result = await owner.Channels.UpdateChannel(spaceId, channelId, null, null, 7, ct);

        Assert.That(result, Is.InstanceOf<FailedUpdateChannel>());
        Assert.That(((FailedUpdateChannel)result).error, Is.EqualTo(UpdateChannelError.SLOW_MODE_NOT_ALLOWED));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task UpdateChannel_RejectsACooldownOnAVoiceChannel(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "the-room", ChannelType.Voice, ct);

        var result = await owner.Channels.UpdateChannel(spaceId, channelId, null, null, 30, ct);

        Assert.That(result, Is.InstanceOf<FailedUpdateChannel>());
        Assert.That(((FailedUpdateChannel)result).error, Is.EqualTo(UpdateChannelError.NOT_A_TEXT_CHANNEL));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task SlowMode_HoldsBackASecondMessageFromAnOrdinaryMember(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var guest     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "slow-chat", ChannelType.Text, ct);

        await JoinAsync(owner, guest, spaceId, ct);
        await owner.Channels.UpdateChannel(spaceId, channelId, null, null, 300, ct);

        await guest.Channels.SendMessage(spaceId, channelId, "first", NoEntities, NextRandomId(), null, ct);

        Assert.That(async () =>
                await guest.Channels.SendMessage(spaceId, channelId, "second", NoEntities, NextRandomId(), null, ct),
            Throws.Exception, "the cooldown let a second message straight through");

        // The exception type crossing the wire is a transport detail; what the feature promises is
        // that the message did not land.
        var messages = await owner.Channels.QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(messages.Values.Count(m => m.sender == guest.UserId), Is.EqualTo(1));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task SlowMode_DoesNotApplyToAModerator(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "slow-chat", ChannelType.Text, ct);

        // The owner archetype carries ManageMessages. Someone who can delete other people's messages
        // is not who the cooldown is aimed at, and throttling them would lock the moderator out of
        // the room they just slowed down.
        await owner.Channels.UpdateChannel(spaceId, channelId, null, null, 300, ct);

        Assert.That(async () =>
        {
            await owner.Channels.SendMessage(spaceId, channelId, "first", NoEntities, NextRandomId(), null, ct);
            await owner.Channels.SendMessage(spaceId, channelId, "second", NoEntities, NextRandomId(), null, ct);
            await owner.Channels.SendMessage(spaceId, channelId, "third", NoEntities, NextRandomId(), null, ct);
        }, Throws.Nothing);

        var messages = await owner.Channels.QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(messages.Values.Count(m => m.sender == owner.UserId), Is.EqualTo(3));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task SlowMode_TurnedOff_StopsHoldingMessagesBack(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var guest     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "was-slow", ChannelType.Text, ct);

        await JoinAsync(owner, guest, spaceId, ct);

        await owner.Channels.UpdateChannel(spaceId, channelId, null, null, 300, ct);
        await guest.Channels.SendMessage(spaceId, channelId, "first", NoEntities, NextRandomId(), null, ct);
        await owner.Channels.UpdateChannel(spaceId, channelId, null, null, 0, ct);

        // Clearing the cooldown has to take effect on the live channel, not only after the grain is
        // next activated — the activation caches the channel row that SendMessage reads.
        Assert.That(async () =>
                await guest.Channels.SendMessage(spaceId, channelId, "second", NoEntities, NextRandomId(), null, ct),
            Throws.Nothing);
    }

    // ── DeleteMessage / ManageMessages ──────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task DeleteMessage_ByItsAuthor_NeedsNoEntitlement(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var guest     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "chat", ChannelType.Text, ct);

        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await guest.Channels.SendMessage(spaceId, channelId, "oops", NoEntities, NextRandomId(), null, ct);

        var result = await guest.Channels.DeleteMessage(spaceId, channelId, messageId, ct);

        Assert.That(result, Is.InstanceOf<SuccessDeleteMessage>(),
            $"Author could not retract their own message: {(result as FailedDeleteMessage)?.error}");

        // Soft delete keeps the row for reports and audit, so the read path is what has to hide it.
        var messages = await owner.Channels.QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(messages.Values.Any(m => m.messageId == messageId), Is.False,
            "a deleted message is still being served to clients");
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task DeleteMessage_OfSomeoneElses_WithoutManageMessages_IsRefused(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var guest     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "chat", ChannelType.Text, ct);

        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "the owner speaks", NoEntities, NextRandomId(), null, ct);

        var result = await guest.Channels.DeleteMessage(spaceId, channelId, messageId, ct);

        Assert.That(result, Is.InstanceOf<FailedDeleteMessage>());
        Assert.That(((FailedDeleteMessage)result).error, Is.EqualTo(DeleteMessageError.INSUFFICIENT_PERMISSIONS));

        var messages = await owner.Channels.QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(messages.Values.Any(m => m.messageId == messageId), Is.True,
            "a refused delete still removed the message");
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task DeleteMessage_OfSomeoneElses_WithManageMessages_Succeeds(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var guest     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "chat", ChannelType.Text, ct);

        await JoinAsync(owner, guest, spaceId, ct);

        var messageId = await guest.Channels.SendMessage(spaceId, channelId, "spam", NoEntities, NextRandomId(), null, ct);

        var result = await owner.Channels.DeleteMessage(spaceId, channelId, messageId, ct);

        Assert.That(result, Is.InstanceOf<SuccessDeleteMessage>(),
            $"Moderator could not take down a member's message: {(result as FailedDeleteMessage)?.error}");

        var messages = await owner.Channels.QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(messages.Values.Any(m => m.messageId == messageId), Is.False);
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task DeleteMessage_Twice_ReportsNotFoundRatherThanSucceedingAgain(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "chat", ChannelType.Text, ct);

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "gone", NoEntities, NextRandomId(), null, ct);

        await owner.Channels.DeleteMessage(spaceId, channelId, messageId, ct);
        var second = await owner.Channels.DeleteMessage(spaceId, channelId, messageId, ct);

        // Otherwise the second call broadcasts a second removal event for a message already gone.
        Assert.That(second, Is.InstanceOf<FailedDeleteMessage>());
        Assert.That(((FailedDeleteMessage)second).error, Is.EqualTo(DeleteMessageError.MESSAGE_NOT_FOUND));
    }

    // ── Voice room invites ──────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task CreateVoiceInviteCode_ThenPreview_NamesTheRoomItPointsAt(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var stranger  = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "rocket-deck", ChannelType.Voice, ct);

        var created = await owner.Channels.CreateVoiceInviteCode(spaceId, channelId, 60, 0, ct);

        Assert.That(created, Is.InstanceOf<SuccessCreateVoiceInvite>(),
            $"Could not mint a room link: {(created as FailedCreateVoiceInvite)?.error}");

        var invite = (SuccessCreateVoiceInvite)created;
        Assert.That(invite.url, Does.StartWith("https://argon.gl/v/"));

        // The whole point of a room link over a space link: the preview sheet can say which room,
        // and the client has the id it needs to Interlink straight after joining.
        var preview = await stranger.Users.PreviewInvite(invite.code, ct);

        Assert.That(preview, Is.InstanceOf<SuccessPreview>(),
            $"Preview failed: {(preview as FailedPreview)?.error}");

        var target = ((SuccessPreview)preview).preview;

        Assert.Multiple(() =>
        {
            Assert.That(target.spaceId, Is.EqualTo(spaceId));
            Assert.That(target.voiceChannelId, Is.EqualTo(channelId));
            Assert.That(target.voiceChannelName, Is.EqualTo("rocket-deck"));
        });
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task CreateVoiceInviteCode_AlsoLetsAStrangerIntoTheSpace(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var stranger  = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "rocket-deck", ChannelType.Voice, ct);

        var created = (SuccessCreateVoiceInvite)await owner.Channels.CreateVoiceInviteCode(spaceId, channelId, 60, 0, ct);

        // One link, two situations: a room invite has to work for someone who is not a member yet,
        // which is exactly what a directed call cannot express.
        var joined = await stranger.Users.JoinToSpace(created.code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"A room link did not admit a non-member: {(joined as FailedJoin)?.error}");
        Assert.That(((SuccessJoin)joined).space.spaceId, Is.EqualTo(spaceId));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task CreateVoiceInviteCode_OnATextChannel_IsRefused(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "just-text", ChannelType.Text, ct);

        var result = await owner.Channels.CreateVoiceInviteCode(spaceId, channelId, 60, 0, ct);

        Assert.That(result, Is.InstanceOf<FailedCreateVoiceInvite>());
        Assert.That(((FailedCreateVoiceInvite)result).error, Is.EqualTo(VoiceInviteError.CHANNEL_IS_NOT_VOICE));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task PreviewInvite_ForAPlainSpaceInvite_LeavesTheRoomUnset(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        var spaceId  = await CreateSpaceAsync(owner, ct);

        var code    = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var preview = await stranger.Users.PreviewInvite(code, ct);

        Assert.That(preview, Is.InstanceOf<SuccessPreview>());

        // A space invite must not start claiming to point at a room, or every ordinary invite would
        // drop the joiner into whichever channel happened to sort first.
        var target = ((SuccessPreview)preview).preview;

        Assert.Multiple(() =>
        {
            Assert.That(target.voiceChannelId, Is.Null);
            Assert.That(target.voiceChannelName, Is.Null);
        });
    }
}
