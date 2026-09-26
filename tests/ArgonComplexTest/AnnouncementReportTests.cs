namespace ArgonComplexTest.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Argon.Entities;
using ArgonContracts;
using ConsoleContracts;
using ArgonComplexTest.Infrastructure;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A post in an announcement channel is reported the way a text-channel message is: members who
/// cannot post there can still report it, and it reaches the moderation queue as a message case.
/// </summary>
[TestFixture]
public class AnnouncementReportTests : ReportTestBase
{
    private async Task<(TestUserSession Owner, TestUserSession First, TestUserSession Second, Guid SpaceId, Guid ChannelId)>
        NewsAsync(CancellationToken ct)
    {
        var owner  = await CreateSessionAsync(ct);
        var first  = await CreateSessionAsync(ct);
        var second = await CreateSessionAsync(ct);

        var spaceId   = await ChannelTestKit.CreateSpaceAsync(owner, ct);
        var channelId = await ChannelTestKit.CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        await JoinAsync(owner, first, spaceId, ct);
        await JoinAsync(owner, second, spaceId, ct);

        return (owner, first, second, spaceId, channelId);
    }

    [Test, CancelAfter(240_000)]
    public async Task Members_report_an_announcement_and_it_reaches_moderation(CancellationToken ct = default)
    {
        var (owner, first, second, spaceId, channelId) = await NewsAsync(ct);

        var messageId = await SayAsync(owner, spaceId, channelId, "buy cheap crypto in the news channel", ct);
        Assert.That(async () => await SayAsync(first, spaceId, channelId, "me too", ct), Throws.Exception,
            "premise: members cannot post here");

        await FileAsync(first, Report(MessageTarget(owner.UserId, channelId, messageId)), ct);
        await FileAsync(second, Report(MessageTarget(owner.UserId, channelId, messageId)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case   = await FindCaseAsync(admin, owner.UserId, ct);
        var details = await admin.GetReportCase(@case.caseId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.target.kind, Is.EqualTo(ReportTargetKind.MESSAGE));
            Assert.That(@case.target.channelId, Is.EqualTo(channelId));
            Assert.That(@case.target.messageId, Is.EqualTo((ulong)messageId));
            Assert.That(@case.reportCount, Is.EqualTo(2));
            Assert.That(@case.independentReporterCount, Is.EqualTo(2));
            Assert.That(@case.status, Is.EqualTo(ReportStatus.PENDING));
            Assert.That(details.contentSnapshot, Does.Contain("buy cheap crypto in the news channel"));
            Assert.That(details.reports.Values.Select(r => r.reporterId), Is.EquivalentTo(new[] { first.UserId, second.UserId }));
        });

        var resolved = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.DELETE_CONTENT, "spam", ct);
        Assert.That(resolved.success, Is.True, resolved.error);

        var left = await first.Channels.QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(left.Values.Select(m => m.messageId), Does.Not.Contain(messageId), "the announcement is still up");
    }

    private static async Task<(SuccessCreateWebhook Created, ArgonMessage Message)> PostThroughWebhookAsync(
        TestUserSession owner, TestUserSession reader, Guid spaceId, Guid channelId, string text, CancellationToken ct)
    {
        var created = (SuccessCreateWebhook)await owner.Client.ForService<IChannelWebhookInteraction>(ChannelTestKit.Services)
           .CreateWebhook(spaceId, channelId, "feed", ct);

        using var posted = await ArgonTestEnvironment.Instance.HttpClient.PostAsync(new Uri(created.url).AbsolutePath,
            new StringContent(JsonSerializer.Serialize(new { content = text }), Encoding.UTF8, "application/json"), ct);
        Assert.That(posted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var message = (await reader.Channels.QueryMessages(spaceId, channelId, null, 50, ct)).Values.Single(m => m.text == text);
        return (created, message);
    }

    /// <summary>The case about one channel message, whoever it names as the target.</summary>
    private static async Task<AdminReportCaseSummary> FindMessageCaseAsync(IAdminConsole admin, Guid channelId, long messageId, CancellationToken ct)
    {
        for (var offset = 0; offset < 4000; offset += 200)
        {
            var page = await admin.GetReportCases(null, null, 200, offset, ct);
            var hit  = page.cases.Values.FirstOrDefault(c => c.target.channelId == channelId && c.target.messageId == (ulong)messageId);

            if (hit is not null)
                return hit;

            if (page.cases.Size < 200)
                break;
        }

        Assert.Fail($"no case about message {messageId} in the queue");
        return null!;
    }

    [Test, CancelAfter(240_000)]
    public async Task A_webhook_post_is_charged_to_the_webhooks_creator_and_taken_down_like_any_other(CancellationToken ct = default)
    {
        var (owner, first, _, spaceId, channelId) = await NewsAsync(ct);
        var message = (await PostThroughWebhookAsync(owner, first, spaceId, channelId, "scam link from a feed", ct)).Message;

        Assert.That(message.sender, Is.EqualTo(UserEntity.SystemUser), "premise: a webhook post is sent as the system user");

        // The client reports the sender it was shown, which is the system user.
        await FileAsync(first, Report(MessageTarget(message.sender, channelId, message.messageId)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case   = await FindMessageCaseAsync(admin, channelId, message.messageId, ct);
        var details = await admin.GetReportCase(@case.caseId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(@case.target.targetId, Is.EqualTo(owner.UserId), "the case is not against the webhook's creator");
            Assert.That(details.contentSnapshot, Does.Contain("scam link from a feed"));
        });

        var resolved = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.DELETE_CONTENT, "scam", ct);
        Assert.That(resolved.success, Is.True, resolved.error);
        Assert.That((await first.Channels.QueryMessages(spaceId, channelId, null, 50, ct)).Values, Is.Empty);
    }

    [Test, CancelAfter(240_000)]
    public async Task A_post_whose_webhook_is_gone_names_no_person_and_person_actions_are_refused(CancellationToken ct = default)
    {
        var (owner, first, _, spaceId, channelId) = await NewsAsync(ct);
        var (created, message) = await PostThroughWebhookAsync(owner, first, spaceId, channelId, "orphaned feed post", ct);

        Assert.That(await owner.Client.ForService<IChannelWebhookInteraction>(ChannelTestKit.Services)
           .DeleteWebhook(spaceId, channelId, created.webhook.webhookId, ct), Is.True);

        await FileAsync(first, Report(MessageTarget(message.sender, channelId, message.messageId)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var @case = await FindMessageCaseAsync(admin, channelId, message.messageId, ct);
        Assert.That(@case.target.targetId, Is.EqualTo(UserEntity.SystemUser), "premise: with the webhook gone only the sender is left");

        var before = await UserRowAsync(UserEntity.SystemUser, ct);

        foreach (var action in new[] { ReportActionKind.WARN_USER, ReportActionKind.MUTE_USER, ReportActionKind.RESTRICT_USER, ReportActionKind.BAN_USER })
        {
            var refused = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, action, "no", ct);
            Assert.That(refused.success, Is.False, $"{action} was applied to the system account");
        }

        var after = await UserRowAsync(UserEntity.SystemUser, ct);
        Assert.Multiple(() =>
        {
            Assert.That(after.LockdownReason, Is.EqualTo(before.LockdownReason), "the system account was locked down");
            Assert.That(after.LockDownExpiration, Is.EqualTo(before.LockDownExpiration));
        });

        var removed = await ResolveAsync(admin, @case.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.DELETE_CONTENT, "spam", ct);
        Assert.That(removed.success, Is.True, removed.error);
    }

    [Test, CancelAfter(240_000)]
    public async Task A_crosspost_copy_that_hides_its_author_is_charged_to_the_source_author(CancellationToken ct = default)
    {
        var publisher = await CreateSessionAsync(ct);
        var admin     = await CreateSessionAsync(ct);
        var reader    = await CreateSessionAsync(ct);

        var sourceSpace = await ChannelTestKit.CreateSpaceAsync(publisher, ct);
        var news        = await ChannelTestKit.CreateChannelAsync(publisher, sourceSpace, "news", ChannelType.Announcement, ct);
        var targetSpace = await ChannelTestKit.CreateSpaceAsync(admin, ct);
        var general     = await ChannelTestKit.CreateChannelAsync(admin, targetSpace, "general", ChannelType.Text, ct);

        await JoinAsync(publisher, admin, sourceSpace, ct);
        await JoinAsync(admin, reader, targetSpace, ct);

        var follows = admin.Client.ForService<IChannelFollowInteraction>(ChannelTestKit.Services);
        Assert.That(await follows.FollowChannel(sourceSpace, news, targetSpace, general, ct), Is.InstanceOf<SuccessFollowChannel>());

        var post      = await SayAsync(publisher, sourceSpace, news, "buy my coin, says the newsletter", ct);
        var published = await publisher.Client.ForService<IChannelFollowInteraction>(ChannelTestKit.Services)
           .PublishMessage(sourceSpace, news, post, ct);
        Assert.That(published, Is.InstanceOf<SuccessPublishMessage>());

        var copy = (await ChannelTestKit.PollAsync(() => reader.Channels.QueryMessages(targetSpace, general, null, 50, ct),
                h => h.Values.Any(m => m.crosspost is not null), TimeSpan.FromSeconds(20), ct))
           .Values.Single(m => m.crosspost is not null);

        // A copy that hides its author is sent as the system user.
        await using (var db = await NewDbAsync(ct))
            await db.Messages
               .Where(m => m.SpaceId == targetSpace && m.ChannelId == general && m.MessageId == copy.messageId)
               .ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatorId, UserEntity.SystemUser), ct);

        await FileAsync(reader, Report(MessageTarget(UserEntity.SystemUser, general, copy.messageId)), ct);

        var (scope, console) = Admin();
        await using var _ = scope;

        var @case = await FindMessageCaseAsync(console, general, copy.messageId, ct);
        Assert.That(@case.target.targetId, Is.EqualTo(publisher.UserId), "the copy's case is not against the source post's author");
    }
}
