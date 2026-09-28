namespace ArgonComplexTest.Tests;

using System.Security.Cryptography;
using Argon.Entities;
using Argon.Features.Storage;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// An upload described before it happens: the hash, size, type and name go first, and the server
/// answers with a copy of a file the account can already see, or with the ticket.
/// </summary>
/// <remarks>
/// The rule under test is who is handed a copy of what. An account finds its own upload again by the
/// hash it claimed for it; anyone else is matched only against a hash the server computed itself, and
/// only where they can read the channel the file was posted in. A hash the caller may not match gets
/// the same answer as new bytes, so the method is not an oracle for what the store holds.
/// </remarks>
[TestFixture]
public class PrepareUploadTests : TestBase
{
    private TestUserSession owner  = null!;
    private TestUserSession member = null!;
    private Guid            spaceId;
    private Guid            channelA;
    private Guid            channelB;

    private FileGcService Gc => FactoryAsp.Services.GetServices<IHostedService>().OfType<FileGcService>().Single();

    private static FileLimitsOptions Limits
        => ArgonTestEnvironment.Instance.Host.Services.GetRequiredService<IOptions<FileLimitsOptions>>().Value;

    [OneTimeSetUp]
    public async Task CreateSpaceAsync()
    {
        owner  = await CreateSessionAsync();
        member = await CreateSessionAsync();

        spaceId  = await SpaceGroupSupport.CreateSpaceAsync(owner, "Prepared");
        channelA = await CreateChannelAsync(owner, spaceId, "a");
        channelB = await CreateChannelAsync(owner, spaceId, "b");

        await SpaceGroupSupport.JoinAsync(owner, member, spaceId);
    }

    /// <summary>Bytes nobody has get a ticket, and what the client declared is what the file is called and typed.</summary>
    [Test, CancelAfter(120_000)]
    public async Task New_bytes_get_a_ticket_and_the_file_keeps_what_was_declared(CancellationToken ct = default)
    {
        var bytes = Fresh();

        var prepared = await owner.Channels.PrepareUploadAttachment(spaceId, channelA, Sha(bytes), bytes.Length, "image/png", "cat.png", ct);

        Assert.That(prepared, Is.InstanceOf<UploadRequired>(), $"refused: {(prepared as FailedPrepareUpload)?.error}");

        var info = await FinishAsync(owner, channelA, (UploadRequired)prepared, bytes, ct);
        var row  = await FileRowAsync(info.fileId, ct);
        var blob = await BlobAsync(row.BlobId!.Value, ct);

        Assert.Multiple(() =>
        {
            Assert.That(info.fileName, Is.EqualTo("cat.png"));
            Assert.That(info.contentType, Is.EqualTo("image/png"));
            Assert.That(row.FileName, Is.EqualTo("cat.png"), "the declared name was not kept on the file");
            Assert.That(blob.ClaimedSha256, Is.EqualTo(SHA256.HashData(bytes)), "the claim did not reach the object");
            Assert.That(blob.Sha256, Is.Null, "a claim was written as a hash the server computed");
        });
    }

    /// <summary>The same account, the same bytes, another channel: a copy, from the hash it claimed the first time.</summary>
    [Test, CancelAfter(120_000)]
    public async Task The_same_account_is_handed_a_copy_of_its_own_upload(CancellationToken ct = default)
    {
        var bytes = Fresh();
        var first = await StoreAsync(owner, channelA, bytes, "first.png", ct);

        var again = await owner.Channels.PrepareUploadAttachment(spaceId, channelB, Sha(bytes), bytes.Length, "image/png", "again.png", ct);

        Assert.That(again, Is.InstanceOf<AlreadyStored>(), $"the account's own upload was not offered back: {again.GetType().Name}");

        var copy    = ((AlreadyStored)again).info;
        var copyRow = await FileRowAsync(copy.fileId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(copy.fileId, Is.Not.EqualTo(first.fileId));
            Assert.That(copy.fileName, Is.EqualTo("again.png"));
            Assert.That(copy.fileSize, Is.EqualTo(first.fileSize));
            Assert.That(copyRow.BlobId, Is.EqualTo((await FileRowAsync(first.fileId, ct)).BlobId), "the copy has an object of its own");
            Assert.That(copyRow.ChannelId, Is.EqualTo(channelB));
        });
    }

    /// <summary>
    /// Somebody else's upload is matched only by a hash the server computed, and only for a member who
    /// can read where it was posted; an outsider with the same hash gets the ticket new bytes would get.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Others_are_matched_by_the_servers_hash_and_only_where_they_can_read(CancellationToken ct = default)
    {
        var bytes     = Fresh();
        var ownerFile = await StoreAsync(owner, channelA, bytes, "post.png", ct);

        var early = await member.Channels.PrepareUploadAttachment(spaceId, channelB, Sha(bytes), bytes.Length, "image/png", "mine.png", ct);

        Assert.That(early, Is.InstanceOf<UploadRequired>(), "a claim alone matched somebody else's upload");

        // The member sends the bytes anyway; two objects with one MD5 get the verifier's attention.
        var memberFile = await FinishAsync(member, channelB, (UploadRequired)early, bytes, ct);
        var canonical  = (await FileRowAsync(ownerFile.fileId, ct)).BlobId;

        await UntilAsync(async () => (await FileRowAsync(memberFile.fileId, ct)).BlobId == canonical,
            () => Gc.VerifyBlobsAsync(ct), "the verifier merging the member's upload into the owner's", ct);

        var third = await CreateSessionAsync(ct);
        await SpaceGroupSupport.JoinAsync(owner, third, spaceId, ct);

        var late = await third.Channels.PrepareUploadAttachment(spaceId, channelB, Sha(bytes), bytes.Length, "image/png", "theirs.png", ct);

        var stranger     = await CreateSessionAsync(ct);
        var otherSpace   = await SpaceGroupSupport.CreateSpaceAsync(stranger, "Elsewhere", ct);
        var otherChannel = await CreateChannelAsync(stranger, otherSpace, "private", ct);

        var outside = await stranger.Channels.PrepareUploadAttachment(otherSpace, otherChannel, Sha(bytes), bytes.Length, "image/png", "spy.png", ct);

        Assert.Multiple(async () =>
        {
            Assert.That(late, Is.InstanceOf<AlreadyStored>(), "a member who can read the channel was made to upload bytes the server had hashed");
            Assert.That((await FileRowAsync(((AlreadyStored)late).info.fileId, ct)).BlobId, Is.EqualTo(canonical));
            Assert.That(outside, Is.InstanceOf<UploadRequired>(), "somebody outside the space learned the store holds these bytes");
        });
    }

    /// <summary>A size over the limit is refused before a URL is signed.</summary>
    [Test, CancelAfter(120_000)]
    public async Task An_over_limit_size_is_refused_before_anything_is_signed(CancellationToken ct = default)
    {
        var prepared = await owner.Channels.PrepareUploadAttachment(spaceId, channelA, Sha(SocialHarness.Png),
            Limits.AttachmentBaseMaxBytes + 1, "application/octet-stream", "big.bin", ct);

        Assert.That(prepared, Is.InstanceOf<FailedPrepareUpload>());
        Assert.That(((FailedPrepareUpload)prepared).error, Is.EqualTo(PrepareUploadError.TOO_LARGE));
    }

    /// <summary>A direct-chat upload records its conversation, comes back to its sender as a copy, and can be copied by the other side.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_direct_chat_upload_records_its_conversation(CancellationToken ct = default)
    {
        var bytes = Fresh();

        var prepared = await owner.Chats.PrepareUploadAttachment(member.UserId, Sha(bytes), bytes.Length, "image/png", "dm.png", ct);

        Assert.That(prepared, Is.InstanceOf<UploadRequired>(), $"refused: {(prepared as FailedPrepareUpload)?.error}");

        var ticket = (UploadRequired)prepared;
        await SocialHarness.UploadAsync(new SuccessUploadFile(ticket.blobId, ticket.uploadUrl, ticket.formFields, ticket.ttlSeconds), bytes, "image/png");
        var sent = await owner.Chats.CompleteUploadAttachment(member.UserId, ticket.blobId, ct);

        var again  = await owner.Chats.PrepareUploadAttachment(member.UserId, Sha(bytes), bytes.Length, "image/png", "dm2.png", ct);
        var byPeer = await member.Channels.AttachExistingFile(spaceId, channelB, sent.fileId, null, ct);

        Assert.Multiple(async () =>
        {
            Assert.That((await FileRowAsync(sent.fileId, ct)).ChannelId, Is.Not.Null, "the direct-chat file does not record its conversation");
            Assert.That(again, Is.InstanceOf<AlreadyStored>(), "the sender's own direct-chat upload was not offered back");
            Assert.That(byPeer, Is.InstanceOf<SuccessAttachExistingFile>(), "the other side of the chat could not copy the file");
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static byte[] Fresh()
        => [..SocialHarness.Png, ..RandomNumberGenerator.GetBytes(16)];

    private static IonBytes Sha(byte[] bytes)
        => new(SHA256.HashData(bytes));

    /// <summary>Prepare, PUT, Complete: the whole path a client takes for new bytes.</summary>
    private async Task<AttachmentInfo> StoreAsync(TestUserSession who, Guid channelId, byte[] bytes, string name, CancellationToken ct)
    {
        var prepared = await who.Channels.PrepareUploadAttachment(spaceId, channelId, Sha(bytes), bytes.Length, "image/png", name, ct);

        Assert.That(prepared, Is.InstanceOf<UploadRequired>(), $"setup: expected a ticket, got {prepared.GetType().Name}");

        return await FinishAsync(who, channelId, (UploadRequired)prepared, bytes, ct);
    }

    private async Task<AttachmentInfo> FinishAsync(TestUserSession who, Guid channelId, UploadRequired ticket, byte[] bytes, CancellationToken ct)
    {
        await SocialHarness.UploadAsync(new SuccessUploadFile(ticket.blobId, ticket.uploadUrl, ticket.formFields, ticket.ttlSeconds), bytes, "image/png");

        return await who.Channels.CompleteUploadAttachment(spaceId, channelId, ticket.blobId, ct);
    }

    private static async Task<Guid> CreateChannelAsync(TestUserSession who, Guid space, string name, CancellationToken ct = default)
    {
        await who.Channels.CreateChannel(space, Guid.Empty, new CreateChannelRequest(space, name, ChannelType.Text, "PrepareUploadTests", null), ct).Ok();

        var channels = await who.Servers.GetChannels(space, ct);

        return channels.Values.Single(c => c.channel.name == name).channel.channelId;
    }

    private async Task<FileEntity> FileRowAsync(Guid id, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        return await db.Files.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == id, ct);
    }

    private async Task<BlobEntity> BlobAsync(Guid id, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        return await db.Blobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == id, ct);
    }

    private async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    /// <summary>Runs a sweep until the condition holds; a pass can lose the lease to the loop, so one is never enough.</summary>
    private static async Task UntilAsync(Func<Task<bool>> done, Func<Task> pass, string what, CancellationToken ct, int attempts = 40)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await pass();

            if (await done())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        Assert.Fail($"{what} did not happen in {attempts} passes");
    }
}
