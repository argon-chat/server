namespace ArgonComplexTest.Tests;

using Argon.Api.Features.AdminApi;
using Argon.Entities;
using Argon.Features.Admin;
using Argon.Grains.Interfaces;
using ArgonContracts;
using ConsoleContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What every report fixture needs: a space with a room to say things in, people to say and
/// report them, and an operator console to decide with.
/// </summary>
/// <remarks>
/// <para>Every scenario builds its own owner, guests, space and messages. Fixtures run in parallel
/// against one server, so the only thing a test may look for in the queue is a case about a user
/// it registered itself — which is how <see cref="FindCaseAsync"/> is written.</para>
///
/// <para>The console is driven directly rather than over its Ion port, as <c>AdminConsoleTests</c>
/// does and for the reason given there: the port is guarded by an operator JWT against a live
/// JWKS endpoint, and the interceptor's only output is the ambient
/// <see cref="OperatorRequestContext"/>, which is set here by hand.</para>
/// </remarks>
public abstract class ReportTestBase : TestBase
{
    protected static readonly Guid OperatorId = Guid.Parse("00000000-0000-0000-0000-0000000ad002");

    protected static IonArray<IMessageEntity> NoEntities => new([]);

    protected (AsyncServiceScope Scope, IAdminConsole Console) Admin()
    {
        var scope = FactoryAsp.Services.CreateAsyncScope();

        OperatorRequestContext.Set(new OperatorRequestContextData
        {
            UserId                = Guid.Parse("00000000-0000-0000-0000-0000000ad003"),
            OperatorId            = OperatorId,
            Email                 = "moderator@argon.test",
            CertificateThumbprint = "TEST-THUMBPRINT"
        });

        return (scope, scope.ServiceProvider.GetRequiredService<IAdminConsole>());
    }

    protected async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    /// <summary>A space with one text channel, owned by <paramref name="owner"/>.</summary>
    protected async Task<(Guid SpaceId, Guid ChannelId)> CreateRoomAsync(TestUserSession owner, CancellationToken ct)
    {
        var created = await owner.Users.CreateSpace(new CreateServerRequest("Reported Space", "Description", string.Empty), ct);

        if (created is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(created as FailedCreateSpace)?.error}");
            return default;
        }

        var spaceId = success.space.spaceId;

        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, "general", ChannelType.Text, "Test channel", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var channel  = channels.Values.FirstOrDefault(c => c.channel.name == "general");

        Assert.That(channel, Is.Not.Null, "the channel should exist after creation");

        return (spaceId, channel!.channel.channelId);
    }

    /// <summary>Puts <paramref name="guest"/> in the space as an ordinary member.</summary>
    protected async Task JoinAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(), $"Guest could not join: {(joined as FailedJoin)?.error}");
    }

    protected static Task<long> SayAsync(TestUserSession author, Guid spaceId, Guid channelId, string text, CancellationToken ct)
        => author.Channels.SendMessage(spaceId, channelId, text, NoEntities, Random.Shared.NextInt64(1, long.MaxValue), null, ct);

    protected static ReportTarget MessageTarget(Guid authorId, Guid channelId, long messageId)
        => new(ReportTargetKind.MESSAGE, authorId, channelId, (ulong)messageId);

    protected static ReportTarget UserTarget(Guid userId)
        => new(ReportTargetKind.USER, userId, null, null);

    protected static CreateReportInput Report(
        ReportTarget target,
        ReportCategory category = ReportCategory.SPAM,
        ReportReason reason = ReportReason.SPAM_OTHER,
        string? note = null)
        => new(target, category, reason, note, null);

    /// <summary>Files, insisting on the acknowledgement, and returns the receipt.</summary>
    protected static async Task<Guid> FileAsync(TestUserSession reporter, CreateReportInput input, CancellationToken ct)
    {
        var result = await reporter.Reports.SubmitReport(input, ct);

        Assert.That(result, Is.InstanceOf<SuccessSubmitReport>(), $"report refused: {(result as FailedSubmitReport)?.error}");

        return ((SuccessSubmitReport)result).reportId;
    }

    /// <summary>
    /// The newest case about a target. Pages through the queue because other fixtures fill it too.
    /// </summary>
    protected async Task<AdminReportCaseSummary> FindCaseAsync(IAdminConsole admin, Guid targetId, CancellationToken ct, bool openOnly = false)
    {
        for (var offset = 0; offset < 4000; offset += 200)
        {
            var page = await admin.GetReportCases(null, null, 200, offset, ct);

            var hit = page.cases.Values.FirstOrDefault(c =>
                c.target.targetId == targetId
             && (!openOnly || c.status is ReportStatus.PENDING or ReportStatus.UNDER_REVIEW or ReportStatus.ESCALATED));

            if (hit is not null)
                return hit;

            if (page.cases.Size < 200)
                break;
        }

        Assert.Fail($"no case about {targetId} in the queue");
        return null!;
    }

    protected static async Task<UserActionResult> ResolveAsync(
        IAdminConsole admin, Guid caseId, ReportStatus status, ReportActionKind action, string? note, CancellationToken ct)
        => await admin.ResolveReportCase(new ResolveReportCaseInput(caseId, status, note, action), ct);

    protected async Task<UserEntity> UserRowAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
    }

    protected Task<UserTrustInfo> TrustOfAsync(Guid userId, CancellationToken ct)
        => GetGrainFactory().GetGrain<IUserTrustGrain>(userId).RecalculateTrustAsync(ct);
}
