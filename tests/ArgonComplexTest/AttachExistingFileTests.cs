namespace ArgonComplexTest.Tests;

using Argon.Entities;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Copying a file into another chat without uploading it again: who may take which file where, and
/// what the copy is.
/// </summary>
/// <remarks>
/// Through the Ion services, the way the desktop client will call them, because the rule under test is
/// the one the grains apply at the door — the target's AttachFiles, the source's readability — rather
/// than the storage grain's, which <c>FileDedupTests</c> covers.
/// </remarks>
[TestFixture]
public class AttachExistingFileTests : TestBase
{
    private TestUserSession owner  = null!;
    private TestUserSession member = null!;
    private Guid            spaceId;
    private Guid            sourceChannel;
    private Guid            targetChannel;

    [OneTimeSetUp]
    public async Task CreateSpaceAsync()
    {
        owner  = await CreateSessionAsync();
        member = await CreateSessionAsync();

        spaceId = await SpaceGroupSupport.CreateSpaceAsync(owner, "Copies");

        sourceChannel = await CreateChannelAsync(owner, spaceId, "source");
        targetChannel = await CreateChannelAsync(owner, spaceId, "target");

        await SpaceGroupSupport.JoinAsync(owner, member, spaceId);
    }

    /// <summary>The owner's own attachment goes into another channel as a new file over the same object.</summary>
    [Test, CancelAfter(120_000)]
    public async Task An_own_attachment_is_copied_into_another_channel_as_a_new_file(CancellationToken ct = default)
    {
        var source = await UploadAsync(owner, sourceChannel, ct);

        var result = await owner.Channels.AttachExistingFile(spaceId, targetChannel, source.fileId, "copy.png", ct);

        Assert.That(result, Is.InstanceOf<SuccessAttachExistingFile>(), $"refused: {(result as FailedAttachExistingFile)?.error}");

        var copy = ((SuccessAttachExistingFile)result).info;

        await using var db = await NewDbAsync(ct);

        var rows = await db.Files.AsNoTracking()
           .Where(f => f.Id == source.fileId || f.Id == copy.fileId)
           .ToDictionaryAsync(f => f.Id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(copy.fileId, Is.Not.EqualTo(source.fileId));
            Assert.That(copy.fileName, Is.EqualTo("copy.png"));
            Assert.That(copy.fileSize, Is.EqualTo(source.fileSize));
            Assert.That(copy.contentType, Is.EqualTo(source.contentType));
            Assert.That(rows[copy.fileId].BlobId, Is.EqualTo(rows[source.fileId].BlobId), "the copy has an object of its own");
            Assert.That(rows[copy.fileId].ChannelId, Is.EqualTo(targetChannel));
            Assert.That(rows[copy.fileId].OwnerId, Is.EqualTo(owner.UserId));
        });
    }

    /// <summary>A member who can read the channel a file was posted in may copy it into a channel they can post in.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_member_who_can_read_the_source_channel_copies_someone_elses_attachment(CancellationToken ct = default)
    {
        var source = await UploadAsync(owner, sourceChannel, ct);

        var result = await member.Channels.AttachExistingFile(spaceId, targetChannel, source.fileId, null, ct);

        Assert.That(result, Is.InstanceOf<SuccessAttachExistingFile>(), $"refused: {(result as FailedAttachExistingFile)?.error}");

        await using var db = await NewDbAsync(ct);

        var copy = await db.Files.AsNoTracking().SingleAsync(f => f.Id == ((SuccessAttachExistingFile)result).info.fileId, ct);

        Assert.That(copy.OwnerId, Is.EqualTo(member.UserId), "the copy belongs to the uploader of the original rather than to the copier");
    }

    /// <summary>A file posted where the caller cannot read is not theirs to copy, whoever they are elsewhere.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_file_from_a_space_the_caller_is_not_in_is_refused(CancellationToken ct = default)
    {
        var stranger     = await CreateSessionAsync(ct);
        var otherSpace   = await SpaceGroupSupport.CreateSpaceAsync(stranger, "Elsewhere", ct);
        var otherChannel = await CreateChannelAsync(stranger, otherSpace, "private", ct);
        var source       = await UploadAsync(stranger, otherSpace, otherChannel, ct);

        var result = await owner.Channels.AttachExistingFile(spaceId, targetChannel, source.fileId, null, ct);

        Assert.That(result, Is.InstanceOf<FailedAttachExistingFile>());
        Assert.That(((FailedAttachExistingFile)result).error, Is.EqualTo(AttachExistingFileError.NOT_AUTHORIZED));
    }

    /// <summary>A source nobody has is reported as such, not as a refusal.</summary>
    [Test, CancelAfter(120_000)]
    public async Task An_unknown_source_is_not_found(CancellationToken ct = default)
    {
        var result = await owner.Channels.AttachExistingFile(spaceId, targetChannel, Guid.NewGuid(), null, ct);

        Assert.That(result, Is.InstanceOf<FailedAttachExistingFile>());
        Assert.That(((FailedAttachExistingFile)result).error, Is.EqualTo(AttachExistingFileError.SOURCE_NOT_FOUND));
    }

    /// <summary>Without AttachFiles in the target there is no copy, however readable the source.</summary>
    [Test, CancelAfter(120_000)]
    public async Task Copying_needs_the_right_to_attach_in_the_target(CancellationToken ct = default)
    {
        var outsider = await CreateSessionAsync(ct);
        var source   = await UploadAsync(owner, sourceChannel, ct);

        var result = await outsider.Channels.AttachExistingFile(spaceId, targetChannel, source.fileId, null, ct);

        Assert.That(result, Is.InstanceOf<FailedAttachExistingFile>());
        Assert.That(((FailedAttachExistingFile)result).error, Is.EqualTo(AttachExistingFileError.NOT_AUTHORIZED));
    }

    /// <summary>
    /// A channel attachment the caller can read goes into a direct chat, and a direct-chat file comes
    /// out again for either side of that chat — and for nobody else.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_direct_chat_file_belongs_to_both_sides_of_the_chat_and_to_nobody_else(CancellationToken ct = default)
    {
        var third = await CreateSessionAsync(ct);
        await SpaceGroupSupport.JoinAsync(owner, third, spaceId, ct);

        var channelFile = await UploadAsync(owner, sourceChannel, ct);

        var intoChat = await member.Chats.AttachExistingFile(owner.UserId, channelFile.fileId, "for-you.png", ct);

        Assert.That(intoChat, Is.InstanceOf<SuccessAttachExistingFile>(), $"refused: {(intoChat as FailedAttachExistingFile)?.error}");

        var begin = await owner.Chats.BeginUploadAttachment(member.UserId, ct);
        Assert.That(begin, Is.InstanceOf<SuccessUploadFile>(), "setup: the direct upload was refused");

        var ticket = (SuccessUploadFile)begin;
        await SocialHarness.UploadAsync(ticket, SocialHarness.Png, "image/png");
        var chatFile = await owner.Chats.CompleteUploadAttachment(member.UserId, ticket.blobId, ct);

        var byPeer    = await member.Channels.AttachExistingFile(spaceId, targetChannel, chatFile.fileId, null, ct);
        var bySender  = await owner.Channels.AttachExistingFile(spaceId, targetChannel, chatFile.fileId, null, ct);
        var byOutside = await third.Channels.AttachExistingFile(spaceId, targetChannel, chatFile.fileId, null, ct);

        Assert.Multiple(() =>
        {
            Assert.That(((SuccessAttachExistingFile)intoChat).info.fileName, Is.EqualTo("for-you.png"));
            Assert.That(byPeer, Is.InstanceOf<SuccessAttachExistingFile>(), "the person a direct-chat file was sent to could not copy it");
            Assert.That(bySender, Is.InstanceOf<SuccessAttachExistingFile>(), "the sender could not reuse their own direct-chat file");
            Assert.That(byOutside, Is.InstanceOf<FailedAttachExistingFile>(), "somebody outside the chat copied a direct-chat file");
            Assert.That(((FailedAttachExistingFile)byOutside).error, Is.EqualTo(AttachExistingFileError.NOT_AUTHORIZED));
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private Task<AttachmentInfo> UploadAsync(TestUserSession who, Guid channelId, CancellationToken ct)
        => UploadAsync(who, spaceId, channelId, ct);

    private static async Task<AttachmentInfo> UploadAsync(TestUserSession who, Guid space, Guid channelId, CancellationToken ct)
    {
        var begin = await who.Channels.BeginUploadAttachment(space, channelId, ct);

        Assert.That(begin, Is.InstanceOf<SuccessUploadFile>(), $"setup: the upload was refused: {(begin as FailedUploadFile)?.error}");

        var ticket = (SuccessUploadFile)begin;
        await SocialHarness.UploadAsync(ticket, SocialHarness.Png, "image/png");

        return await who.Channels.CompleteUploadAttachment(space, channelId, ticket.blobId, ct);
    }

    private static async Task<Guid> CreateChannelAsync(TestUserSession who, Guid space, string name, CancellationToken ct = default)
    {
        await who.Channels.CreateChannel(space, Guid.Empty, new CreateChannelRequest(space, name, ChannelType.Text, "AttachExistingFileTests", null), ct).Ok();

        var channels = await who.Servers.GetChannels(space, ct);

        return channels.Values.Single(c => c.channel.name == name).channel.channelId;
    }

    private async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);
}
