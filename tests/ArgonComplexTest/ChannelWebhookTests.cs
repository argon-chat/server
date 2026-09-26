namespace ArgonComplexTest.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using static ChannelTestKit;

/// <summary>
/// Incoming webhooks: channel managers create them (in announcement channels too), anyone holding the
/// URL posts plain text as the webhook, and nothing else about the URL can be learned from outside.
/// </summary>
[TestFixture]
public class ChannelWebhookTests : TestBase
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private static IChannelWebhookInteraction Hooks(TestUserSession session)
        => session.Client.ForService<IChannelWebhookInteraction>(Services);

    private async Task<(TestUserSession Owner, TestUserSession Guest, Guid SpaceId, Guid ChannelId)> NewsAsync(CancellationToken ct)
    {
        var owner = await CreateSessionAsync(ct);
        var guest = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        await JoinAsync(owner, guest, spaceId, ct);

        return (owner, guest, spaceId, channelId);
    }

    private static async Task<SuccessCreateWebhook> CreateAsync(TestUserSession owner, Guid spaceId, Guid channelId, string name,
        CancellationToken ct)
    {
        var created = await Hooks(owner).CreateWebhook(spaceId, channelId, name, ct);
        Assert.That(created, Is.InstanceOf<SuccessCreateWebhook>(), $"refused: {(created as FailedCreateWebhook)?.error}");
        return (SuccessCreateWebhook)created;
    }

    /// <summary>The path of the URL the server handed out, for the in-process client.</summary>
    private static string PathOf(SuccessCreateWebhook created) => new Uri(created.url).AbsolutePath;

    private static Task<HttpResponseMessage> PostRawAsync(string path, string body, CancellationToken ct)
        => ArgonTestEnvironment.Instance.HttpClient.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"), ct);

    private static Task<HttpResponseMessage> PostAsync(string path, object payload, CancellationToken ct)
        => PostRawAsync(path, JsonSerializer.Serialize(payload), ct);

    private static async Task<List<ArgonMessage>> ReadAsync(TestUserSession reader, Guid spaceId, Guid channelId, CancellationToken ct)
        => (await reader.Channels.QueryMessages(spaceId, channelId, null, 50, ct)).Values.ToList();

    [Test, CancelAfter(120_000)]
    public async Task A_manager_creates_a_webhook_in_an_announcement_channel_and_its_url_posts(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);

        var created = await CreateAsync(owner, spaceId, channelId, "  Release bot  ", ct);

        Assert.Multiple(() =>
        {
            Assert.That(created.webhook.name, Is.EqualTo("Release bot"), "the name was not trimmed");
            Assert.That(created.webhook.channelId, Is.EqualTo(channelId));
            Assert.That(created.webhook.creatorId, Is.EqualTo(owner.UserId));
            Assert.That(created.url, Is.EqualTo($"https://api.test.local/api/webhooks/{created.webhook.webhookId}/{created.token}"));
            Assert.That(created.token.Length, Is.GreaterThanOrEqualTo(40));
        });

        await using (var db = await DbAsync(ct))
        {
            var row = await db.ChannelWebhooks.AsNoTracking().SingleAsync(w => w.Id == created.webhook.webhookId, ct);
            Assert.That(row.TokenHash, Is.Not.EqualTo(created.token).And.EqualTo(ChannelWebhookEntity.HashToken(created.token)),
                "the token is stored as it was handed out");
        }

        using var response = await PostAsync(PathOf(created), new { content = "v2.1 is out" }, ct);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), await response.Content.ReadAsStringAsync(ct));

        var posted = (await ReadAsync(guest, spaceId, channelId, ct)).Single();
        Assert.Multiple(() =>
        {
            Assert.That(posted.text, Is.EqualTo("v2.1 is out"));
            Assert.That(posted.sender, Is.EqualTo(UserEntity.SystemUser));
            Assert.That(posted.webhook, Is.Not.Null, "the message does not say which webhook posted it");
            Assert.That(posted.webhook!.webhookId, Is.EqualTo(created.webhook.webhookId));
            Assert.That(posted.webhook.name, Is.EqualTo("Release bot"));
        });

        var listed = await Hooks(owner).GetWebhooks(spaceId, channelId, ct);
        Assert.That(await PollAsync(async () => (await Hooks(owner).GetWebhooks(spaceId, channelId, ct)).Values.Single().lastUsedAt,
            at => at is not null, Window, ct), Is.Not.Null, "lastUsedAt was never set");
        Assert.That(listed.Values.Select(w => w.webhookId), Is.EqualTo(new[] { created.webhook.webhookId }));
    }

    [Test, CancelAfter(120_000)]
    public async Task Only_a_member_with_manage_channels_manages_webhooks(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);
        var created = await CreateAsync(owner, spaceId, channelId, "hook", ct);
        var id      = created.webhook.webhookId;

        var create     = await Hooks(guest).CreateWebhook(spaceId, channelId, "mine", ct);
        var list       = await Hooks(guest).GetWebhooks(spaceId, channelId, ct);
        var rename     = await Hooks(guest).RenameWebhook(spaceId, channelId, id, "stolen", ct);
        var regenerate = await Hooks(guest).RegenerateWebhookToken(spaceId, channelId, id, ct);
        var delete     = await Hooks(guest).DeleteWebhook(spaceId, channelId, id, ct);

        Assert.Multiple(() =>
        {
            Assert.That((create as FailedCreateWebhook)?.error, Is.EqualTo(ChannelWebhookError.INSUFFICIENT_PERMISSIONS));
            Assert.That(list.Values, Is.Empty, "a member without ManageChannels listed the webhooks");
            Assert.That((rename as FailedUpdateWebhook)?.error, Is.EqualTo(ChannelWebhookError.INSUFFICIENT_PERMISSIONS));
            Assert.That((regenerate as FailedCreateWebhook)?.error, Is.EqualTo(ChannelWebhookError.INSUFFICIENT_PERMISSIONS));
            Assert.That(delete, Is.False);
        });

        // A role with ManageChannels on this channel is enough.
        var archetypes = ArchetypesOf(owner);
        var role       = await archetypes.CreateArchetype(spaceId, "integrations", ct);
        Assert.That(await archetypes.SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, guest.UserId, ct), role.id, true, ct),
            Is.True);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, role.id,
            deny: ArgonEntitlement.None, allow: ArgonEntitlement.ManageChannels, ct);

        Assert.That(await PollAsync(() => Hooks(guest).CreateWebhook(spaceId, channelId, "mine", ct),
            r => r is SuccessCreateWebhook, Window, ct), Is.InstanceOf<SuccessCreateWebhook>());

        var renamed = await Hooks(owner).RenameWebhook(spaceId, channelId, id, "  renamed ", ct);
        Assert.That((renamed as SuccessUpdateWebhook)?.webhook.name, Is.EqualTo("renamed"));
    }

    [Test, CancelAfter(120_000)]
    public async Task Names_are_checked_the_type_is_checked_and_a_channel_holds_ten(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var textId  = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);
        var voiceId = await CreateChannelAsync(owner, spaceId, "lounge", ChannelType.Voice, ct);

        var empty   = await Hooks(owner).CreateWebhook(spaceId, textId, "   ", ct);
        var tooLong = await Hooks(owner).CreateWebhook(spaceId, textId, new string('x', 33), ct);
        var inVoice = await Hooks(owner).CreateWebhook(spaceId, voiceId, "hook", ct);

        Assert.Multiple(() =>
        {
            Assert.That((empty as FailedCreateWebhook)?.error, Is.EqualTo(ChannelWebhookError.NAME_EMPTY));
            Assert.That((tooLong as FailedCreateWebhook)?.error, Is.EqualTo(ChannelWebhookError.NAME_TOO_LONG));
            Assert.That((inVoice as FailedCreateWebhook)?.error, Is.EqualTo(ChannelWebhookError.NOT_A_TEXT_CHANNEL));
        });

        for (var i = 0; i < ChannelWebhookEntity.MaxPerChannel; i++)
            await CreateAsync(owner, spaceId, textId, $"hook {i}", ct);

        var eleventh = await Hooks(owner).CreateWebhook(spaceId, textId, "one more", ct);
        Assert.That((eleventh as FailedCreateWebhook)?.error, Is.EqualTo(ChannelWebhookError.LIMIT_REACHED));

        var first = (await Hooks(owner).GetWebhooks(spaceId, textId, ct)).Values.First();
        Assert.That(await Hooks(owner).DeleteWebhook(spaceId, textId, first.webhookId, ct), Is.True);
        Assert.That(await Hooks(owner).CreateWebhook(spaceId, textId, "after a delete", ct), Is.InstanceOf<SuccessCreateWebhook>(),
            "a deleted webhook still counted against the limit");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_wrong_token_and_an_unknown_webhook_get_the_same_404(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);
        var created = await CreateAsync(owner, spaceId, channelId, "hook", ct);

        using var wrongToken = await PostAsync($"/api/webhooks/{created.webhook.webhookId}/not-the-token", new { content = "hi" }, ct);
        using var unknown    = await PostAsync($"/api/webhooks/{Guid.NewGuid()}/{created.token}", new { content = "hi" }, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(wrongToken.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await wrongToken.Content.ReadAsStringAsync(ct), Is.EqualTo(await unknown.Content.ReadAsStringAsync(ct)),
                "the two answers differ, so a token can be told from an id");
            Assert.That(await ReadAsync(guest, spaceId, channelId, ct), Is.Empty);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Empty_too_long_and_malformed_posts_are_refused_with_400(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);
        var path = PathOf(await CreateAsync(owner, spaceId, channelId, "hook", ct));

        using var empty        = await PostAsync(path, new { content = "   " }, ct);
        using var tooLong      = await PostAsync(path, new { content = new string('a', 4097) }, ct);
        using var noContent    = await PostAsync(path, new { username = "x" }, ct);
        using var notJson      = await PostRawAsync(path, "content=hello", ct);
        using var numeric      = await PostRawAsync(path, "{\"content\": 42}", ct);
        using var longUsername = await PostAsync(path, new { content = "hi", username = new string('u', 33) }, ct);
        using var atTheLimit   = await PostAsync(path, new { content = new string('a', 4096) }, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(tooLong.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(noContent.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(notJson.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(numeric.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(longUsername.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(atTheLimit.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "a post at exactly the text limit was refused");
            Assert.That((await ReadAsync(guest, spaceId, channelId, ct)).Count, Is.EqualTo(1));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_webhook_posts_thirty_times_a_minute_and_then_gets_429(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);
        var path = PathOf(await CreateAsync(owner, spaceId, channelId, "chatty", ct));
        var other = PathOf(await CreateAsync(owner, spaceId, channelId, "quiet", ct));

        for (var i = 1; i <= IncomingWebhookGrainLimit; i++)
        {
            using var ok = await PostAsync(path, new { content = $"post {i}" }, ct);
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), $"post {i} of {IncomingWebhookGrainLimit} was refused");
        }

        using var limited = await PostAsync(path, new { content = "one too many" }, ct);
        using var sibling = await PostAsync(other, new { content = "the other webhook" }, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(limited.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(limited.Headers.RetryAfter?.Delta?.TotalSeconds, Is.GreaterThan(0).And.LessThanOrEqualTo(60));
            Assert.That(sibling.StatusCode, Is.EqualTo(HttpStatusCode.NoContent), "the limit is not per webhook");
            Assert.That((await ReadAsync(guest, spaceId, channelId, ct)).Count, Is.EqualTo(IncomingWebhookGrainLimit + 1));
        });
    }

    private const int IncomingWebhookGrainLimit = 30;

    [Test, CancelAfter(120_000)]
    public async Task A_webhook_message_is_plain_text_carries_its_author_and_pings_nobody(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);
        var created = await CreateAsync(owner, spaceId, channelId, "Status page", ct);

        await using var observer = await RealtimeClient.ConnectAsync(guest, ct);
        await observer.SubscribeToSpace(spaceId, ct);
        var mark = observer.Mark();

        const string text = "@everyone the API is down, see https://status.example.com";
        using var response = await PostAsync(PathOf(created), new { content = text, username = "Incident #42" }, ct);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var sent = await observer.WaitForAsync<MessageSent>(e => e.message.channelId == channelId, Window, mark, ct);

        Assert.Multiple(() =>
        {
            Assert.That(sent.message.text, Is.EqualTo(text));
            Assert.That(sent.message.entities.Values, Is.Empty, "a webhook message carried entities");
            Assert.That(sent.message.webhook?.name, Is.EqualTo("Incident #42"), "the username did not override the name");
            Assert.That(sent.message.webhook?.webhookId, Is.EqualTo(created.webhook.webhookId));
        });

        await observer.AssertNoneWithinAsync<BatchMentionOccurred>(e => e.channelId == channelId, TimeSpan.FromSeconds(2),
            "a webhook message pinged the space", from: mark, ct: ct);
        Assert.That(await MentionsAsync(guest, channelId, ct), Is.Zero);

        // Unread counts see it like any message.
        var unread = await PollAsync(async () => (await guest.Users.GetGlobalBadges(ct)).spaces.Values
               .FirstOrDefault(s => s.spaceId == spaceId)?.unreadChannelCount ?? 0,
            n => n >= 1, Window, ct);
        Assert.That(unread, Is.GreaterThanOrEqualTo(1), "the webhook message left the channel read");

        var stored = await StoredMessageAsync(spaceId, channelId, sent.message.messageId, ct);
        Assert.That(stored!.Webhook, Is.EqualTo(new MessageWebhookAuthor(created.webhook.webhookId, "Incident #42", null)));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_new_token_retires_the_old_url_and_a_rename_shows_on_the_next_post(CancellationToken ct = default)
    {
        var (owner, guest, spaceId, channelId) = await NewsAsync(ct);
        var created = await CreateAsync(owner, spaceId, channelId, "before", ct);

        // Warm the webhook's cache with the old token first.
        using (var first = await PostAsync(PathOf(created), new { content = "one" }, ct))
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var regenerated = await Hooks(owner).RegenerateWebhookToken(spaceId, channelId, created.webhook.webhookId, ct);
        Assert.That(regenerated, Is.InstanceOf<SuccessCreateWebhook>());
        var fresh = (SuccessCreateWebhook)regenerated;

        Assert.That(await Hooks(owner).RenameWebhook(spaceId, channelId, created.webhook.webhookId, "after", ct),
            Is.InstanceOf<SuccessUpdateWebhook>());

        using var old   = await PostAsync(PathOf(created), new { content = "old url" }, ct);
        using var @new  = await PostAsync(PathOf(fresh), new { content = "new url" }, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(fresh.token, Is.Not.EqualTo(created.token));
            Assert.That(fresh.webhook.webhookId, Is.EqualTo(created.webhook.webhookId));
            Assert.That(old.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "the old URL still posts");
            Assert.That(@new.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

            var read = await ReadAsync(guest, spaceId, channelId, ct);
            Assert.That(read.Select(m => m.webhook?.name), Is.EquivalentTo(new[] { "before", "after" }));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Deleting_the_webhook_or_its_channel_stops_its_url(CancellationToken ct = default)
    {
        var (owner, _, spaceId, channelId) = await NewsAsync(ct);
        var otherId = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);

        var deleted = await CreateAsync(owner, spaceId, channelId, "deleted", ct);
        var kept    = await CreateAsync(owner, spaceId, channelId, "kept", ct);
        var other   = await CreateAsync(owner, spaceId, otherId, "other channel", ct);

        using (var warm = await PostAsync(PathOf(deleted), new { content = "warm" }, ct))
            Assert.That(warm.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        Assert.That(await Hooks(owner).DeleteWebhook(spaceId, channelId, deleted.webhook.webhookId, ct), Is.True);
        Assert.That(await Hooks(owner).DeleteWebhook(spaceId, channelId, deleted.webhook.webhookId, ct), Is.False);

        using (var gone = await PostAsync(PathOf(deleted), new { content = "after delete" }, ct))
            Assert.That(gone.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        using (var warm = await PostAsync(PathOf(kept), new { content = "warm" }, ct))
            Assert.That(warm.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await owner.Channels.DeleteChannel(spaceId, channelId, ct);

        using var afterChannel = await PostAsync(PathOf(kept), new { content = "into a deleted channel" }, ct);
        using var elsewhere    = await PostAsync(PathOf(other), new { content = "still here" }, ct);

        await using var db = await DbAsync(ct);
        var left = await db.ChannelWebhooks.AsNoTracking().Where(w => w.SpaceId == spaceId).Select(w => w.Id).ToListAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(afterChannel.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "a deleted channel's webhook still posts");
            Assert.That(elsewhere.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(left, Is.EqualTo(new[] { other.webhook.webhookId }), "the deleted channel's webhooks are still stored");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Deleting_the_space_removes_its_webhooks(CancellationToken ct = default)
    {
        var (owner, _, spaceId, channelId) = await NewsAsync(ct);
        var created = await CreateAsync(owner, spaceId, channelId, "hook", ct);

        using (var warm = await PostAsync(PathOf(created), new { content = "warm" }, ct))
            Assert.That(warm.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        await Grains.GetGrain<ISpaceDeletionGrain>(spaceId).DeleteNowAsync(owner.UserId);

        using var after = await PostAsync(PathOf(created), new { content = "into a deleted space" }, ct);

        await using var db = await DbAsync(ct);
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(await db.ChannelWebhooks.AsNoTracking().AnyAsync(w => w.SpaceId == spaceId, ct), Is.False);
        });
    }
}
