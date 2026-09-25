namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Features.Integrations.Klipy;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ChannelTestKit;

/// <summary>
/// What a message may carry and what the server does to it on the way in — attachments, GIFs, link
/// cards — together with the channel's settings sheet, a moderator's removal, and the durable copy of
/// the channel's newest message id.
/// </summary>
/// <remarks>
/// The common thread is that the client does not get the last word. A download URL, a GIF's preview,
/// a link's card: each is something a client could write and the server must overwrite, because what
/// the server stores is served to everybody else in the channel.
/// </remarks>
[TestFixture]
public class ChannelMessagingTests : TestBase
{
    private static IonArray<IMessageEntity> Entities(params IMessageEntity[] entities) => new(entities);

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private static MessageEntityAttachment Attachment(Guid fileId, string? downloadUrl = null)
        => new(EntityType.Attachment, 0, 0, 1, fileId, "photo.png", 1024, "image/png", 64, 64, null, downloadUrl);

    private async Task<(TestUserSession Owner, Guid SpaceId, Guid ChannelId)> RoomAsync(string name, CancellationToken ct)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, name, ChannelType.Text, ct);
        return (owner, spaceId, channelId);
    }

    private static async Task<ArgonMessage> ReadBackAsync(TestUserSession reader, Guid spaceId, Guid channelId, long messageId, CancellationToken ct)
        => (await reader.Channels.QueryMessages(spaceId, channelId, null, 50, ct)).Values.Single(m => m.messageId == messageId);

    // ── Attachments ─────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Attachments_need_AttachFiles_and_a_message_carries_at_most_ten(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync("files", ct);
        var guest = await CreateSessionAsync(ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var eleven = Enumerable.Range(0, 11).Select(_ => (IMessageEntity)Attachment(Guid.NewGuid())).ToArray();

        Assert.That(async () => await owner.Channels.SendMessage(spaceId, channelId, "too many", Entities(eleven), NextRandomId(), null, ct),
            Throws.Exception, "an eleventh attachment was accepted");

        var ten = await owner.Channels.SendMessage(spaceId, channelId, "just enough", Entities(eleven[..10]), NextRandomId(), null, ct);
        Assert.That((await ReadBackAsync(owner, spaceId, channelId, ten, ct)).entities.Values, Has.Count.EqualTo(10));

        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.AttachFiles, ct);

        Assert.That(async () => await guest.Channels.SendMessage(spaceId, channelId, "sneaky", Entities(Attachment(Guid.NewGuid())),
                NextRandomId(), null, ct),
            Throws.Exception, "a member denied AttachFiles posted an attachment");

        // The deny is about files, not about talking.
        Assert.That(async () => await guest.Channels.SendMessage(spaceId, channelId, "words are fine", Entities(), NextRandomId(), null, ct),
            Throws.Nothing);

        var messages = await owner.Channels.QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(messages.Values.Select(m => m.text), Is.EquivalentTo(new[] { "just enough", "words are fine" }));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_download_url_written_by_the_client_is_replaced_by_the_servers_own(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync("urls", ct);
        var fileId = Guid.NewGuid();

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "look",
            Entities(Attachment(fileId, "https://evil.example/steal?c=")), NextRandomId(), null, ct);

        var stored   = await StoredMessageAsync(spaceId, channelId, messageId, ct);
        var served   = (MessageEntityAttachment)(await ReadBackAsync(owner, spaceId, channelId, messageId, ct)).entities.Values.Single();
        var expected = Services.GetRequiredService<IS3StorageService>().GetFileDownloadUrl(fileId);

        Assert.Multiple(() =>
        {
            Assert.That(((MessageEntityAttachment)stored!.Entities.Single()).downloadUrl, Is.Null,
                "the client's URL was stored, so every later reader gets it");
            Assert.That(served.downloadUrl, Is.EqualTo(expected), "the served URL is not the one the server builds for the file");
        });
    }

    // ── GIFs ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A GIF is signed for the user who picked it, and a signed one is served from the copy the
    /// server already holds rather than from anything the client said about it.
    /// </summary>
    /// <remarks>
    /// The cached copy is seeded as the row <c>KlipyService.EnsureCachedAsync</c> looks for first, so
    /// the test never reaches the Klipy API; an uncached slug would.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_forged_gif_is_dropped_and_a_signed_one_is_served_from_the_cached_copy(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync("gifs", ct);

        var klipy  = Services.GetRequiredService<IKlipyService>();
        var slug   = $"dancing-cat-{Guid.NewGuid():N}";
        var fileId = Guid.NewGuid();

        await using (var db = await DbAsync(ct))
        {
            db.Files.Add(new FileEntity
            {
                Id          = fileId,
                OwnerId     = Guid.Empty,
                Purpose     = FilePurpose.Gif,
                S3Key       = klipy.ComputeCachePath(slug),
                BucketName  = "cdn",
                FileSize    = 2048,
                ContentType = "image/webp",
                Finalized   = true
            });
            db.FileCounters.Add(new FileCounterEntity { Id = fileId, RefCount = 1 });
            await db.SaveChangesAsync(ct);
        }

        var forged = new MessageEntityGif(EntityType.Gif, 0, 0, 1, "forged-slug", "00", null, 100, 100, null);
        var signed = new MessageEntityGif(EntityType.Gif, 0, 0, 1, slug, klipy.ComputeUserHmac(slug, owner.UserId), null, 100, 100,
            "https://evil.example/preview.webp");

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "", Entities(forged, signed), NextRandomId(), null, ct);

        var gifs = (await ReadBackAsync(owner, spaceId, channelId, messageId, ct)).entities.Values.OfType<MessageEntityGif>().ToList();

        Assert.That(gifs.Select(g => g.gifId), Is.EqualTo(new[] { slug }), "a GIF whose signature does not match its sender was kept");

        Assert.Multiple(() =>
        {
            Assert.That(gifs[0].fileId, Is.EqualTo(fileId), "the GIF was not pointed at the server's cached copy");
            Assert.That(gifs[0].previewUrl, Is.EqualTo(Services.GetRequiredService<IS3StorageService>().GetFileDownloadUrl(fileId)),
                "the preview the client wrote survived");
        });

        // Signed for the owner, so worthless in anybody else's hands.
        var guest = await CreateSessionAsync(ct);
        await JoinAsync(owner, guest, spaceId, ct);

        var replayed = await guest.Channels.SendMessage(spaceId, channelId, "", Entities(signed), NextRandomId(), null, ct);
        Assert.That((await ReadBackAsync(owner, spaceId, channelId, replayed, ct)).entities.Values.OfType<MessageEntityGif>(), Is.Empty,
            "a signature minted for one user was accepted from another");
    }

    // ── Link cards ──────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Without_PostEmbeddedLinks_the_link_is_sent_and_the_card_is_not(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync("no-embeds", ct);
        var guest = await CreateSessionAsync(ct);
        await JoinAsync(owner, guest, spaceId, ct);

        await DenyOnChannelAsync(owner, spaceId, channelId, ArgonEntitlement.PostEmbeddedLinks, ct);

        const string text = "see https://example.com/embed-me";
        var stub = new MessageEntityLinkPreview(EntityType.LinkPreview, 4, 28, 1, "https://example.com/embed-me", null, null, null, null, null);
        var url  = new MessageEntityUrl(EntityType.Url, 4, 28, 1, "example.com", "/embed-me");

        var messageId = await guest.Channels.SendMessage(spaceId, channelId, text, Entities(url, stub), NextRandomId(), null, ct);
        var served    = await ReadBackAsync(owner, spaceId, channelId, messageId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(served.text, Is.EqualTo(text));
            Assert.That(served.entities.Values.OfType<MessageEntityUrl>(), Has.Exactly(1).Items, "the link itself was taken out");
            Assert.That(served.entities.Values.OfType<MessageEntityLinkPreview>(), Is.Empty, "a card was kept for a member who may not embed");
        });
    }

    // ── Settings ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task UpdateChannel_RefusesANameOrTopicOverTheLimit_AndAcceptsOneAtIt(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync("limits", ct);

        var longName  = await owner.Channels.UpdateChannel(spaceId, channelId, new string('n', 129), null, null, null, ct);
        var longTopic = await owner.Channels.UpdateChannel(spaceId, channelId, null, new string('d', 1025), null, null, ct);
        var atLimit   = await owner.Channels.UpdateChannel(spaceId, channelId, new string('n', 128), new string('d', 1024), null, null, ct);

        Assert.Multiple(() =>
        {
            Assert.That((longName as FailedUpdateChannel)?.error, Is.EqualTo(UpdateChannelError.NAME_TOO_LONG));
            Assert.That((longTopic as FailedUpdateChannel)?.error, Is.EqualTo(UpdateChannelError.DESCRIPTION_TOO_LONG));
            Assert.That(atLimit, Is.InstanceOf<SuccessUpdateChannel>(), $"refused at the limit: {(atLimit as FailedUpdateChannel)?.error}");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task UpdateChannel_RefusesABitrateOnATextChannelAndOneOutOfRangeOnAVoiceChannel(CancellationToken ct = default)
    {
        var (owner, spaceId, textId) = await RoomAsync("chat", ct);
        var voiceId = await CreateChannelAsync(owner, spaceId, "room", ChannelType.Voice, ct);

        var onText = await owner.Channels.UpdateChannel(spaceId, textId, null, null, null, 64, ct);
        var tooLow  = await owner.Channels.UpdateChannel(spaceId, voiceId, null, null, null, 7, ct);
        var tooHigh = await owner.Channels.UpdateChannel(spaceId, voiceId, null, null, null, 321, ct);

        Assert.Multiple(() =>
        {
            Assert.That((onText as FailedUpdateChannel)?.error, Is.EqualTo(UpdateChannelError.NOT_A_VOICE_CHANNEL));
            Assert.That((tooLow as FailedUpdateChannel)?.error, Is.EqualTo(UpdateChannelError.BITRATE_OUT_OF_RANGE));
            Assert.That((tooHigh as FailedUpdateChannel)?.error, Is.EqualTo(UpdateChannelError.BITRATE_OUT_OF_RANGE));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task UpdateChannel_WithNothingNew_AnswersTheChannelAndTellsNobody(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync("steady", ct);
        var lastId = await owner.Channels.SendMessage(spaceId, channelId, "hi", Entities(), NextRandomId(), null, ct);

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        var mark = observer.Mark();

        await PollAsync(async () =>
        {
            await using var db = await DbAsync(ct);
            return await db.ChannelLastMessages.AsNoTracking().Where(m => m.ChannelId == channelId).Select(m => m.LastMessageId)
               .FirstOrDefaultAsync(ct);
        }, id => id == lastId, TimeSpan.FromSeconds(30), ct);

        var result = await owner.Channels.UpdateChannel(spaceId, channelId, "  steady  ", null, null, null, ct);

        Assert.That(result, Is.InstanceOf<SuccessUpdateChannel>());

        var channel = ((SuccessUpdateChannel)result).channel;

        Assert.Multiple(() =>
        {
            Assert.That(channel.name, Is.EqualTo("steady"), "the name is trimmed before it is compared");
            Assert.That(channel.lastMessageId, Is.EqualTo(lastId), "an unchanged channel came back without its stored high-water mark");
        });

        await observer.AssertNoneWithinAsync<ChannelModifiedV2>(e => e.channelId == channelId, TimeSpan.FromSeconds(1),
            "a save that changed nothing was announced to the space", mark, ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task UpdateChannel_OnAChannelThatHasBeenDeleted_IsRefusedAndDoesNotBringItBack(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync("doomed", ct);

        // Wake the grain first, so it is the live activation, holding the channel in memory, that is asked.
        await owner.Channels.SendMessage(spaceId, channelId, "still here", Entities(), NextRandomId(), null, ct);
        await owner.Channels.DeleteChannel(spaceId, channelId, ct);

        var result = await owner.Channels.UpdateChannel(spaceId, channelId, "resurrected", null, null, null, ct);

        // Refused at the permission check: a channel that no longer exists grants nothing, not even to
        // the owner — so the row lookup behind it never runs.
        Assert.That(result, Is.InstanceOf<FailedUpdateChannel>());

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        Assert.That(channels.Values.Select(c => c.channel.channelId), Does.Not.Contain(channelId), "a rename brought a deleted channel back");
    }

    // ── Moderation ──────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_moderation_removal_of_a_message_already_gone_reports_false(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync("moderated", ct);
        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "reported", Entities(), NextRandomId(), null, ct);

        var channel  = Grains.GetGrain<IChannelGrain>(channelId);
        var operator_ = Guid.NewGuid();

        var first  = await channel.DeleteMessageByModeration(messageId, operator_, ct);
        var second = await channel.DeleteMessageByModeration(messageId, operator_, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.False, "a second removal claimed to have removed something");
            Assert.That((await owner.Channels.QueryMessages(spaceId, channelId, null, 10, ct)).Values, Is.Empty);
        });
    }

    // ── The durable high-water mark ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A flush carrying an older id than the one stored leaves the stored one alone.
    /// </summary>
    /// <remarks>
    /// This is the migration race the flush's <c>WHERE LastMessageId &lt; @id</c> exists for: two
    /// activations of one channel alive for a moment, the outgoing one flushing on its way out after
    /// the new one has already written something newer. The newer write is played here by the test,
    /// straight into the row, between a send and the deactivation that flushes it.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_flush_never_moves_the_stored_high_water_mark_backwards(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync("monotonic", ct);
        var channel = Grains.GetGrain<IChannelGrain>(channelId);

        async Task<long> StoredMarkAsync()
        {
            await using var db = await DbAsync(ct);
            return await db.ChannelLastMessages.AsNoTracking().Where(m => m.ChannelId == channelId).Select(m => m.LastMessageId)
               .FirstOrDefaultAsync(ct);
        }

        var first = await owner.Channels.SendMessage(spaceId, channelId, "first", Entities(), NextRandomId(), null, ct);
        await channel.ClearChannel();

        Assert.That(await PollAsync(StoredMarkAsync, id => id == first, TimeSpan.FromSeconds(30), ct), Is.EqualTo(first),
            "premise: the first id reached the row");

        var second = await owner.Channels.SendMessage(spaceId, channelId, "second", Entities(), NextRandomId(), null, ct);
        var newer  = second + 1_000_000;

        await using (var db = await DbAsync(ct))
            await db.ChannelLastMessages.Where(m => m.ChannelId == channelId)
               .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastMessageId, newer), ct);

        await channel.ClearChannel();

        // Long enough for the deactivation flush and a timer tick besides; the row must hold throughout.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        while (DateTimeOffset.UtcNow < deadline)
        {
            Assert.That(await StoredMarkAsync(), Is.EqualTo(newer), "a flush wrote an older id over a newer one");
            await Task.Delay(250, ct);
        }
    }
}
