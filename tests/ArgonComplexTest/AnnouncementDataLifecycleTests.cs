namespace ArgonComplexTest.Tests;

using System.Text.Json;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using ion.runtime;
using static ChannelTestKit;

/// <summary>
/// Announcement posts under the account lifecycle: a data export carries them like any other
/// channel message, and erasing the author keeps them, still attributed to the now deleted account.
/// </summary>
[TestFixture]
public class AnnouncementDataLifecycleTests : TestBase
{
    private static Task<long> PostAsync(TestUserSession author, Guid spaceId, Guid channelId, string text, CancellationToken ct)
        => author.Channels.SendMessage(spaceId, channelId, text, new IonArray<IMessageEntity>([]),
            Random.Shared.NextInt64(1, long.MaxValue), null, ct);

    /// <summary>Lets <paramref name="member"/> post in the announcement channel through a role.</summary>
    private static async Task GrantPostingAsync(TestUserSession owner, TestUserSession member, Guid spaceId, Guid channelId,
        CancellationToken ct)
    {
        var archetypes = ArchetypesOf(owner);
        var role       = await archetypes.CreateArchetype(spaceId, "herald", ct);

        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, member.UserId, ct), role.id, true, ct),
            Is.True);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, role.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.SendMessages, ct);
    }

    private static async Task<long> PostWhenAllowedAsync(TestUserSession author, Guid spaceId, Guid channelId, string text,
        CancellationToken ct)
    {
        var id = await PollAsync(async () =>
        {
            try
            {
                return await PostAsync(author, spaceId, channelId, text, ct);
            }
            catch
            {
                return 0L;
            }
        }, id => id != 0, TimeSpan.FromSeconds(20), ct);

        Assert.That(id, Is.Not.Zero, "the member was never allowed to post");
        return id;
    }

    [Test, CancelAfter(180_000)]
    public async Task An_export_carries_the_accounts_posts_in_announcement_channels(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var herald = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, ct);
        var newsId  = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        await JoinAsync(owner, herald, spaceId, ct);
        await GrantPostingAsync(owner, herald, spaceId, newsId, ct);

        var mine   = await PostWhenAllowedAsync(herald, spaceId, newsId, "herald's announcement", ct);
        var theirs = await PostAsync(owner, spaceId, newsId, "the owner's announcement", ct);

        var requested = await herald.Security.RequestDataExport(ct);
        Assert.That(requested, Is.InstanceOf<SuccessRequestDataExport>(), $"refused: {(requested as FailedRequestDataExport)?.error}");

        var status = await AccountConsoleHarness.WaitForExportAsync(herald, DataExportStatusKind.COMPLETED, ct: ct);
        Assert.That(status.status, Is.EqualTo(DataExportStatusKind.COMPLETED), $"the export stalled in {status.status}");

        var archive = await ExportArchive.DownloadAsync(status.downloadUrl!, ct);
        var file    = $"channels/{spaceId}/{newsId}.json";

        Assert.That(archive.Keys, Does.Contain(file), "the announcement channel is missing from the export");

        using var document = JsonDocument.Parse(archive[file]);
        var ids = document.RootElement.GetProperty("Messages").EnumerateArray()
           .Select(m => m.GetProperty("MessageId").GetInt64())
           .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.EqualTo(new[] { mine }), "the export has someone else's post or lacks the account's own");
            Assert.That(archive[file], Does.Contain("herald's announcement"));
            Assert.That(archive[file], Does.Not.Contain("the owner's announcement"));
            Assert.That(theirs, Is.Not.EqualTo(mine));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Erasing_the_author_keeps_their_announcements_attributed_to_the_deleted_account(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var herald = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, ct);
        var newsId  = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        await JoinAsync(owner, herald, spaceId, ct);
        await GrantPostingAsync(owner, herald, spaceId, newsId, ct);

        var post = await PostWhenAllowedAsync(herald, spaceId, newsId, "posted before leaving for good", ct);

        var erased = await Grains.GetGrain<IAccountDeletionGrain>(herald.UserId).EraseNowAsync();
        Assert.That(erased.Success, Is.True, erased.Error?.ToString());

        var stored = await StoredMessageAsync(spaceId, newsId, post, ct);
        var read   = (await owner.Channels.QueryMessages(spaceId, newsId, null, 50, ct)).Values.SingleOrDefault(m => m.messageId == post);
        var author = await AccountSeed.ReadUserAsync(herald.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.IsDeleted, Is.False, "erasing the author took the announcement down");
            Assert.That(stored.CreatorId, Is.EqualTo(herald.UserId), "the post was re-attributed");
            Assert.That(stored.Text, Is.EqualTo("posted before leaving for good"));
            Assert.That(read, Is.Not.Null, "readers no longer see the announcement");
            Assert.That(read!.sender, Is.EqualTo(herald.UserId));
            Assert.That(author, Is.Not.Null);
            Assert.That(author!.IsDeleted, Is.True);
            Assert.That(author.DisplayName, Is.EqualTo("Deleted Account"), "the author is not shown as a deleted account");
        });
    }
}
