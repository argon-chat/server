namespace ArgonComplexTest.Tests;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What the members of a space see when a bot comes and goes.
/// </summary>
/// <remarks>
/// <para>A bot's presence does not travel the route every other user's presence travels. There is no
/// hub connection, no <c>UserSessionGrain</c>, no heartbeat and no disconnect grace:
/// <see cref="Argon.Api.Grains.BotGatewayGrain"/> writes the presence keys itself when an SSE stream
/// opens and calls <c>SpaceGrain.SetUserStatus</c> directly, bypassing the aggregate, the hysteresis
/// record and everything that keeps a human's status honest. That makes the bot the one account in
/// the product whose "online" is asserted rather than derived, and it is worth pinning what a member
/// of the space actually observes — because a member cannot tell a bot from a person, and neither
/// can the roster.</para>
///
/// <para>The fixture therefore watches from the outside: a human member with a real hub connection
/// records the <c>UserChangedStatus</c> events, and the same claims are cross-checked against
/// <c>GetMemberPresence</c> (what a client loading the space is told) and against Redis (what the
/// rest of the server believes). Where the three disagree, the disagreement is the finding.</para>
///
/// <para>Every test seeds its <em>own</em> bot. Two reasons: the gateway grain is keyed by the bot
/// and carries the open-stream count across tests, and the <c>IEvents</c> bot rate limit is five
/// stream opens per minute per bot — sharing one bot would have made the later tests fail for
/// bookkeeping reasons rather than presence ones.</para>
/// </remarks>
[TestFixture]
public class PresenceBotTests : TestBase
{
    private PresenceProbe    probe = null!;
    private TestUserSession  owner = null!;
    private Guid             spaceId;

    [OneTimeSetUp]
    public async Task PrepareAsync()
    {
        probe   = await PresenceProbe.CreateAsync();
        owner   = await CreateSessionAsync();
        spaceId = await CreateSpaceAsync(owner, "Bot Presence");
    }

    /// <summary>
    /// A bot that opens its event stream is announced Online to the space, and the two other places
    /// a client can read presence from agree with the announcement.
    /// </summary>
    /// <remarks>
    /// This is the baseline every other test here leans on: the event, the snapshot a joining client
    /// loads (<c>GetMemberPresence</c>) and the server's own liveness answer
    /// (<c>IsUserOnline</c>) all have to say the same thing about the same bot at the same moment.
    /// If they do not, nothing that follows about disconnects means anything.
    /// </remarks>
    [Test, CancelAfter(120_000), Order(1)]
    public async Task A_connecting_bot_is_announced_online_to_the_space(CancellationToken ct = default)
    {
        var bot = await SeedBotAsync(ct);
        await InstallBotAsync(owner, spaceId, bot.AppId, ct);

        // The observer connects after the install so that the join's own status broadcast is not in
        // its log: everything recorded from here on is about the bot opening its stream.
        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        var mark = observer.Mark();

        await using var stream = await BotEventStream.OpenAsync(HttpClient, bot.Token, ct);

        var announced = await observer.WaitForRecordAsync<UserChangedStatus>(
            e => e.userId == bot.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), mark, ct);

        var snapshot = await Poll.ForValueAsync(
            async () => (await owner.Servers.GetMemberPresence(spaceId, ct))
               .Values.FirstOrDefault(m => m.userId == bot.UserId)?.status,
            status => status == UserStatus.Online,
            TimeSpan.FromSeconds(10), ct: ct);

        var online   = await probe.IsUserOnlineAsync(bot.UserId, ct);
        var sessions = await probe.ActiveSessionIdsAsync(bot.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(announced.Stream, Is.EqualTo(RealtimeStream.BroadcastSpace),
                "a bot's status has to reach the space group like any other member's");
            Assert.That(announced.SpaceId, Is.EqualTo(spaceId),
                "the bot's status arrived addressed to a different space");
            Assert.That(snapshot, Is.EqualTo(UserStatus.Online),
                "GetMemberPresence does not list the bot as Online although the space was just told it is");
            Assert.That(online, Is.True,
                "IsUserOnline says the bot is not online while its event stream is open");
            Assert.That(sessions, Does.Contain(bot.SessionId),
                $"the bot's gateway session is not in the live-session index (index=[{string.Join(", ", sessions)}])");
            Assert.That(observer.DecodeFailures, Is.Empty);
        });
    }

    /// <summary>
    /// When a bot's event stream closes, the bot stops being online everywhere — the space is told,
    /// and the server itself stops believing the bot has a live session.
    /// </summary>
    /// <remarks>
    /// <para>Guards both halves of a bot's teardown, because they are written by two different calls
    /// and only one of them used to be made. The <em>event</em> half — the space is told Offline, the
    /// session status key is deleted and the aggregate recomputes — comes from
    /// <c>RemoveSessionStatusAsync</c> and always worked. The <em>liveness</em> half — the presence
    /// key <c>ConnectAsync</c> wrote with <c>SetSessionOnlineAsync</c>, which is what
    /// <c>IsUserOnlineAsync</c> and <c>GetActiveSessionIdsAsync</c> (and therefore
    /// <c>IUserSessionDiscoveryService</c>, and therefore <c>CallGrain</c>'s "is this bot reachable"
    /// check) actually read — needs <c>RemoveSessionAsync</c>, and that call was missing.</para>
    ///
    /// <para>Defect S23, now fixed: <c>BotGatewayGrain.DisconnectAsync</c>
    /// (src/Argon.Api/Grains/BotGatewayGrain.cs) makes both calls, the pairing the human teardown
    /// paths use. Left unpaired, <c>presence:user:{bot}:session:bot_{bot:N}</c> was simply left to
    /// lapse on its 120 s TTL with nothing to refresh it, so for up to two minutes after a bot
    /// process died the server still believed it had a live session: this test measured 110 s of TTL
    /// still to run five seconds after the stream closed. Both readings are polled to the same
    /// five-second deadline the Offline event gets — the contract is that a closed stream takes the
    /// bot offline promptly, by every reading, not eventually.</para>
    /// </remarks>
    [Test, CancelAfter(120_000), Order(2)]
    public async Task A_bot_whose_stream_closes_stops_being_online_everywhere(CancellationToken ct = default)
    {
        var bot = await SeedBotAsync(ct);
        await InstallBotAsync(owner, spaceId, bot.AppId, ct);

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        var beforeConnect = observer.Mark();

        var stream = await BotEventStream.OpenAsync(HttpClient, bot.Token, ct);

        await observer.WaitForAsync<UserChangedStatus>(
            e => e.userId == bot.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeConnect, ct);

        var beforeClose = observer.Mark();
        var clock       = Stopwatch.StartNew();

        await stream.CloseAsync();

        var offline = await observer.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == bot.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(5), beforeClose, ct);
        var offlineAfter = clock.Elapsed;

        // Both readings are polled to the same five-second deadline the event got: the claim is that
        // a closed stream takes the bot offline promptly, not eventually.
        var stillOnline = await Poll.ForValueAsync(
            () => probe.IsUserOnlineAsync(bot.UserId, ct),
            value => !value, TimeSpan.FromSeconds(5), ct: ct);

        var liveSessions = await Poll.ForValueAsync(
            () => probe.ActiveSessionIdsAsync(bot.UserId, ct),
            list => list.Count == 0, TimeSpan.FromSeconds(5), ct: ct);

        var presenceKey = PresenceProbe.PresenceSessionKey(bot.UserId, bot.SessionId);
        var presenceTtl = await probe.TtlOf(presenceKey);
        var statusKey   = PresenceProbe.SessionStatusKey(bot.UserId, bot.SessionId);
        var statusLives = await probe.Exists(statusKey);
        var aggregated  = await probe.AggregatedStatusAsync(bot.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(offline, Is.Not.Null,
                $"the space was never told the bot went offline after its stream closed.{observer.Dump(beforeClose)}");
            Assert.That(offlineAfter, Is.LessThan(TimeSpan.FromSeconds(5)),
                "the Offline broadcast took longer than the five seconds a member would tolerate");
            Assert.That(statusLives, Is.False,
                "the bot's session status key outlived its stream");
            Assert.That(aggregated, Is.EqualTo(UserStatus.Offline),
                "the bot's aggregated status is not Offline after its only session ended");
            Assert.That(stillOnline, Is.False,
                $"IsUserOnline still reports the bot online after its stream closed: the gateway never deleted " +
                $"{presenceKey}, which still has {presenceTtl?.TotalSeconds ?? -1:F0} s of TTL left to run");
            Assert.That(liveSessions, Is.Empty,
                $"GetActiveSessionIds still lists the dead gateway session ([{string.Join(", ", liveSessions)}]); " +
                $"{presenceKey} TTL = {presenceTtl?.TotalSeconds ?? -1:F0} s");
        });
    }

    /// <summary>
    /// A bot that reconnects is announced Online once, not twice.
    /// </summary>
    /// <remarks>
    /// A reconnect is the most common thing a bot does — a deploy, a dropped stream, a restart — and
    /// every one of them fans a status out to every member of every installed space. A duplicate here
    /// is not cosmetic: the desktop client feeds each event into <c>userStore.updateUserStatus</c>
    /// and re-sorts the member list, so a doubled Online is a doubled repaint for everyone in the
    /// space, on every bot restart.
    /// </remarks>
    [Test, CancelAfter(120_000), Order(3)]
    public async Task A_reconnecting_bot_is_announced_online_exactly_once(CancellationToken ct = default)
    {
        var bot = await SeedBotAsync(ct);
        await InstallBotAsync(owner, spaceId, bot.AppId, ct);

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        var beforeFirstConnect = observer.Mark();

        var first = await BotEventStream.OpenAsync(HttpClient, bot.Token, ct);
        await observer.WaitForAsync<UserChangedStatus>(
            e => e.userId == bot.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeFirstConnect, ct);

        var beforeClose = observer.Mark();
        await first.CloseAsync();
        await observer.WaitForAsync<UserChangedStatus>(
            e => e.userId == bot.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(10), beforeClose, ct);

        var beforeReconnect = observer.Mark();
        await using var second = await BotEventStream.OpenAsync(HttpClient, bot.Token, ct);

        await observer.WaitForAsync<UserChangedStatus>(
            e => e.userId == bot.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeReconnect, ct);

        // A fixed wait, deliberately: the assertion is that a *second* Online does not arrive, and an
        // absence has no edge to poll for. Three seconds is far inside the gateway's 30 s presence
        // tick, so nothing legitimate can land in it.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var onlines = observer.EventsOfType<UserChangedStatus>(beforeReconnect)
           .Where(e => e.userId == bot.UserId && e.status == UserStatus.Online)
           .ToList();

        var snapshot = await Poll.ForValueAsync(
            async () => (await owner.Servers.GetMemberPresence(spaceId, ct))
               .Values.FirstOrDefault(m => m.userId == bot.UserId)?.status,
            status => status == UserStatus.Online,
            TimeSpan.FromSeconds(10), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(onlines, Has.Count.EqualTo(1),
                $"a bot reconnecting to one space produced {onlines.Count} Online broadcasts." +
                observer.Dump(beforeReconnect));
            Assert.That(snapshot, Is.EqualTo(UserStatus.Online),
                "the reconnected bot is not Online in the space snapshot");
        });
    }

    /// <summary>
    /// A bot flapping offline and back does not move a human member's status.
    /// </summary>
    /// <remarks>
    /// <para>A regression guard rather than a hypothesis. <c>BotGatewayGrain</c> writes presence with
    /// <c>SetSessionStatusAsync</c> and broadcasts with a direct <c>SpaceGrain.SetUserStatus</c>,
    /// never going through <c>UserGrain.AggregateAndBroadcastStatusAsync</c> — so it never touches
    /// <c>status:user:{u}:lastbroadcast</c>, the hysteresis record shared by every user. The record is
    /// per-user, and this test is what would notice if it stopped being: a bot cycling its own status
    /// must not suppress, re-emit or overwrite the status of the human sitting in the same space.</para>
    ///
    /// <para>The human is a session of its own — never detached, never reconnected — so the only
    /// thing that can move its status during the window is the bot.</para>
    /// </remarks>
    [Test, CancelAfter(120_000), Order(4)]
    public async Task A_bot_flapping_does_not_disturb_a_human_members_status(CancellationToken ct = default)
    {
        var bot = await SeedBotAsync(ct);
        await InstallBotAsync(owner, spaceId, bot.AppId, ct);

        var human = await CreateSessionAsync(ct);
        await JoinAsync(owner, human, spaceId, ct);

        await using var observer    = await RealtimeClient.ConnectAsync(owner, ct);
        await using var humanClient = await RealtimeClient.ConnectAsync(human, ct);

        await humanClient.Heartbeat(UserStatus.DoNotDisturb, ct);
        await observer.WaitForAsync<UserChangedStatus>(
            e => e.userId == human.UserId && e.status == UserStatus.DoNotDisturb,
            TimeSpan.FromSeconds(15), ct: ct);

        var humanBroadcastBefore = await probe.LastBroadcastAsync(human.UserId);
        var beforeFlap           = observer.Mark();

        var first = await BotEventStream.OpenAsync(HttpClient, bot.Token, ct);
        await observer.WaitForAsync<UserChangedStatus>(
            e => e.userId == bot.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeFlap, ct);

        var beforeBotClose = observer.Mark();
        await first.CloseAsync();
        await observer.WaitForAsync<UserChangedStatus>(
            e => e.userId == bot.UserId && e.status == UserStatus.Offline,
            TimeSpan.FromSeconds(10), beforeBotClose, ct);

        var beforeBotReconnect = observer.Mark();
        await using var second = await BotEventStream.OpenAsync(HttpClient, bot.Token, ct);
        await observer.WaitForAsync<UserChangedStatus>(
            e => e.userId == bot.UserId && e.status == UserStatus.Online,
            TimeSpan.FromSeconds(15), beforeBotReconnect, ct);

        var humanEvents = observer.EventsOfType<UserChangedStatus>(beforeFlap)
           .Where(e => e.userId == human.UserId)
           .ToList();

        var humanSnapshot = (await owner.Servers.GetMemberPresence(spaceId, ct))
           .Values.FirstOrDefault(m => m.userId == human.UserId)?.status;

        var humanAggregate      = await probe.AggregatedStatusAsync(human.UserId, ct);
        var humanBroadcastAfter = await probe.LastBroadcastAsync(human.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(humanEvents, Is.Empty,
                $"the bot's disconnect/reconnect produced status events for the human member: " +
                $"[{string.Join(", ", humanEvents.Select(e => e.status))}]");
            Assert.That(humanAggregate, Is.EqualTo(UserStatus.DoNotDisturb),
                "the human's aggregated status changed while only the bot was moving");
            Assert.That(humanSnapshot, Is.EqualTo(UserStatus.DoNotDisturb),
                "GetMemberPresence stopped reporting the human as DoNotDisturb while only the bot was moving");
            Assert.That(humanBroadcastAfter, Is.EqualTo(humanBroadcastBefore),
                "the bot's direct SetUserStatus path rewrote the human's hysteresis record");
        });
    }

    /// <summary>
    /// Installing a bot that is already connected into a second space announces it Online there once.
    /// </summary>
    /// <remarks>
    /// <para>One transition, one event. Installing a bot walks two paths that both used to announce
    /// it: <c>SpaceGrain.InstallBot</c> calls <c>AddMemberAsync</c> → <c>UserJoined</c>, and a few
    /// lines later <c>gateway.SubscribeToSpace(spaceId)</c>. This pins that exactly one of them
    /// speaks — the join, which is the one that runs whether or not the gateway is up — and that the
    /// snapshot agrees with what it said.</para>
    ///
    /// <para>Defect S4 (bot leg), now fixed on both sides: <c>UserJoined</c> announces the joiner's
    /// real aggregated status instead of a flat Online, and <c>BotGatewayGrain.SubscribeToSpace</c>
    /// no longer announces at all. Before that, two identical <c>UserChangedStatus(bot, Online)</c>
    /// arrived 12 ms apart on the new space's group, and every connected member paid for the second
    /// one with a redundant repaint on every install. The three-second wait after the first event is
    /// a fixed window on purpose: the claim is that no second event follows, and three seconds sits
    /// well inside the gateway's 30 s presence tick.</para>
    /// </remarks>
    [Test, CancelAfter(120_000), Order(5)]
    public async Task Installing_a_connected_bot_into_a_second_space_announces_it_online_once(
        CancellationToken ct = default)
    {
        var bot = await SeedBotAsync(ct);
        await InstallBotAsync(owner, spaceId, bot.AppId, ct);

        // The second space exists before the observer connects, so the hub puts the observer into
        // both space groups at OnConnectedAsync and no SubscribeToSpace race can hide the event.
        var secondSpaceId = await CreateSpaceAsync(owner, "Bot Presence II");

        await using var stream = await BotEventStream.OpenAsync(HttpClient, bot.Token, ct);

        await using var observer = await RealtimeClient.ConnectAsync(owner, ct);
        var beforeInstall = observer.Mark();

        await InstallBotAsync(owner, secondSpaceId, bot.AppId, ct);

        await observer.WaitForAsync<UserChangedStatus>(
            e => e.userId == bot.UserId && e.status == UserStatus.Online && e.spaceId == secondSpaceId,
            TimeSpan.FromSeconds(15), beforeInstall, ct);

        // Fixed wait for the same reason as in the reconnect test: the claim is that no second event
        // follows, and three seconds is well inside the gateway's 30 s tick.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var announcements = observer.RecordsOfType<UserChangedStatus>(beforeInstall)
           .Where(r => r.SpaceId == secondSpaceId
                    && ((UserChangedStatus)r.Event).userId == bot.UserId
                    && ((UserChangedStatus)r.Event).status == UserStatus.Online)
           .ToList();

        var snapshot = await Poll.ForValueAsync(
            async () => (await owner.Servers.GetMemberPresence(secondSpaceId, ct))
               .Values.FirstOrDefault(m => m.userId == bot.UserId)?.status,
            status => status == UserStatus.Online,
            TimeSpan.FromSeconds(10), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(announcements, Has.Count.EqualTo(1),
                $"installing a connected bot into a space announced it Online {announcements.Count} times." +
                observer.Dump(beforeInstall));
            Assert.That(snapshot, Is.EqualTo(UserStatus.Online),
                "the newly installed bot is not Online in the new space's snapshot");
        });
    }

    // ───────────── space / bot fixtures ─────────────

    private static async Task<Guid> CreateSpaceAsync(TestUserSession session, string name,
        CancellationToken ct = default)
    {
        var result = await session.Users.CreateSpace(
            new CreateServerRequest(name, "Bot presence", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)!.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    private static async Task JoinAsync(TestUserSession host, TestUserSession guest, Guid space,
        CancellationToken ct)
    {
        var code   = await host.Servers.CreateInviteCode(space, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"Guest could not join the space: {(joined as FailedJoin)?.error}");
    }

    /// <summary>A published bot with its own user, dev team and token — the shape the Bot API expects.</summary>
    private sealed record SeededBot(Guid AppId, Guid UserId, string Token)
    {
        /// <summary>The gateway's presence sid: <c>BotGatewayGrain.BotSessionId</c>.</summary>
        public string SessionId => $"bot_{UserId:N}";
    }

    /// <summary>
    /// Seeds a bot straight into the database, the way <c>BotApiTests</c> does — there is no public
    /// flow that creates one, and the developer console path is not what this fixture is testing.
    /// </summary>
    private async Task<SeededBot> SeedBotAsync(CancellationToken ct = default)
    {
        var botUserId = Guid.NewGuid();
        var botAppId  = Guid.NewGuid();
        var botToken  = GenerateBotToken(botAppId);
        var teamId    = Guid.NewGuid();

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        await using var db = await scope.ServiceProvider
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        db.Users.Add(new UserEntity
        {
            Id          = botUserId,
            Username    = $"pbot_{botUserId:N}"[..32],
            DisplayName = "Presence Bot",
            Email       = $"pbot_{botUserId:N}@test.local",
            AgreeTOS    = true,
            DateOfBirth = new DateOnly(2000, 1, 1)
        });

        db.TeamEntities.Add(new DevTeamEntity
        {
            TeamId  = teamId,
            OwnerId = owner.UserId,
            Name    = "Presence Bot Team"
        });
        db.MemberTeamEntities.Add(new DevTeamMemberEntity
        {
            TeamId   = teamId,
            UserId   = owner.UserId,
            JoinedAt = DateTime.UtcNow,
            IsOwner  = true
        });

        db.BotEntities.Add(new BotEntity
        {
            AppId            = botAppId,
            TeamId           = teamId,
            Name             = "Presence Bot App",
            ClientId         = Guid.NewGuid().ToString(),
            ClientSecret     = Guid.NewGuid().ToString(),
            AppType          = DevAppType.Bot,
            BotToken         = botToken,
            BotAsUserId      = botUserId,
            LifecycleState   = BotLifecycleState.Published,
            MaxSpaces        = 100,
            RequiredScopes   = [],
            AllowedRedirects = []
        });

        await db.SaveChangesAsync(ct);

        return new SeededBot(botAppId, botUserId, botToken);
    }

    /// <summary>The <c>{hex app id}:{secret}</c> shape the bot authentication handler parses.</summary>
    private static string GenerateBotToken(Guid botAppId)
    {
        Span<byte> appBytes = stackalloc byte[16];
        botAppId.TryWriteBytes(appBytes);
        appBytes.Reverse();

        Span<byte> secretBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(secretBytes);

        var secret = Convert.ToBase64String(secretBytes)
           .Replace('+', '-')
           .Replace('/', '_')
           .TrimEnd('=');

        return $"{Convert.ToHexString(appBytes)}:{secret}";
    }

    private async Task InstallBotAsync(TestUserSession installer, Guid space, Guid botAppId,
        CancellationToken ct)
    {
        var result = await installer.Client
           .ForService<IBotManagementInteraction>(FactoryAsp.Services)
           .InstallBot(space, botAppId, ct);

        Assert.That(result, Is.InstanceOf<SuccessInstallBot>(),
            $"Could not install the bot: {(result as FailedInstallBot)?.error}");
    }

    // ───────────── the bot's SSE event stream ─────────────

    /// <summary>
    /// One open <c>GET /api/bot/IEvents/v1/Stream</c>, which is what makes
    /// <c>BotGatewayGrain.ConnectAsync</c> run and what closing makes <c>DisconnectAsync</c> run.
    /// </summary>
    /// <remarks>
    /// <para>Modelled on <c>BotApiTests.CollectSseEventsAsync</c>, but held open instead of collected
    /// to a stopping condition: these tests need the stream to be a thing they can end at a chosen
    /// moment, because that moment is the event they are timing.</para>
    ///
    /// <para><see cref="OpenAsync"/> returns only once the gateway has written <c>ready</c>, which it
    /// does after <c>ConnectAsync</c> has registered the presence session — so a test that opens a
    /// stream and immediately asserts on presence is not racing the connect.</para>
    /// </remarks>
    private sealed class BotEventStream : IAsyncDisposable
    {
        private readonly CancellationTokenSource cts;
        private readonly HttpResponseMessage     response;
        private readonly TaskCompletionSource    ready;
        private readonly Task                    pump;

        private BotEventStream(CancellationTokenSource cts, HttpResponseMessage response, Stream body)
        {
            this.cts      = cts;
            this.response = response;
            ready         = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pump          = PumpAsync(body);
        }

        public static async Task<BotEventStream> OpenAsync(HttpClient http, string botToken,
            CancellationToken ct = default)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/bot/IEvents/v1/Stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            Assert.That(response.IsSuccessStatusCode, Is.True,
                $"the bot event stream was refused: {(int)response.StatusCode} {response.StatusCode}");

            var body   = await response.Content.ReadAsStreamAsync(cts.Token);
            var stream = new BotEventStream(cts, response, body);

            try
            {
                await stream.ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            }
            catch (Exception e)
            {
                await stream.DisposeAsync();
                Assert.Fail($"the bot event stream never reported 'ready', so the gateway never connected: {e.Message}");
            }

            return stream;
        }

        private async Task PumpAsync(Stream body)
        {
            try
            {
                using var reader = new StreamReader(body, Encoding.UTF8);

                while (!cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    if (line is null)
                        break;
                    if (line.StartsWith("event: ", StringComparison.Ordinal) && line[7..] == "ready")
                        ready.TrySetResult();
                }
            }
            catch
            {
                // The stream ending — cancelled, aborted or disposed — is how every one of these tests
                // finishes with it; the pump has nothing to report about it.
            }
            finally
            {
                ready.TrySetCanceled();
            }
        }

        /// <summary>Ends the stream the way a bot process dying ends it: the response goes away.</summary>
        public async Task CloseAsync()
        {
            await cts.CancelAsync();
            response.Dispose();

            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Already accounted for by the pump's own catch.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
            cts.Dispose();
        }
    }
}
