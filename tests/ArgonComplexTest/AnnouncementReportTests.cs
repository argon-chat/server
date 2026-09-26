namespace ArgonComplexTest.Tests;

using System.Net;
using System.Text;
using ArgonContracts;
using ConsoleContracts;
using ArgonComplexTest.Infrastructure;

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

    [Test, CancelAfter(240_000)]
    public async Task A_webhook_post_is_reported_and_taken_down_like_any_other(CancellationToken ct = default)
    {
        var (owner, first, _, spaceId, channelId) = await NewsAsync(ct);

        var created = (SuccessCreateWebhook)await owner.Client.ForService<IChannelWebhookInteraction>(ChannelTestKit.Services)
           .CreateWebhook(spaceId, channelId, "feed", ct);

        using var posted = await ArgonTestEnvironment.Instance.HttpClient.PostAsync(new Uri(created.url).AbsolutePath,
            new StringContent("{\"content\":\"scam link from a feed\"}", Encoding.UTF8, "application/json"), ct);
        Assert.That(posted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var message = (await first.Channels.QueryMessages(spaceId, channelId, null, 50, ct)).Values.Single();

        // The client reports the sender it was shown, which is the system user.
        await FileAsync(first, Report(MessageTarget(message.sender, channelId, message.messageId)), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        AdminReportCaseSummary? @case = null;
        for (var offset = 0; offset < 4000 && @case is null; offset += 200)
        {
            var page = await admin.GetReportCases(null, null, 200, offset, ct);
            @case = page.cases.Values.FirstOrDefault(c => c.target.channelId == channelId && c.target.messageId == (ulong)message.messageId);
            if (page.cases.Size < 200)
                break;
        }

        Assert.That(@case, Is.Not.Null, "the report on a webhook post never reached the queue");

        var resolved = await ResolveAsync(admin, @case!.caseId, ReportStatus.RESOLVED_ACTION_TAKEN, ReportActionKind.DELETE_CONTENT, "scam", ct);
        Assert.That(resolved.success, Is.True, resolved.error);
        Assert.That((await first.Channels.QueryMessages(spaceId, channelId, null, 50, ct)).Values, Is.Empty);
    }
}
