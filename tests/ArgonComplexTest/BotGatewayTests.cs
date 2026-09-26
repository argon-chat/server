namespace ArgonComplexTest.Tests;

using System.Text;
using AccountContracts;
using Argon.Features.BotApi;
using Argon.Features.NatsStreaming;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Orleans.Core.Internal;
using Orleans.Runtime;
using static DevTeamsHarness;

/// <summary>
/// The bot's side of the event stream: <c>BotGatewayGrain</c>, which turns a bot's spaces into NATS
/// consumers, filters what comes off them by intent, and keeps the bot online while it listens.
/// </summary>
/// <remarks>
/// <para>The grain is driven the way <c>EventsV1</c> drives it — connect, consume in a loop, disconnect
/// — but called directly rather than through an open SSE response. That is what lets a test say
/// "exactly these events, in this many calls": over SSE the batching, the retries and the order of
/// two consumers are all hidden behind a text stream. <c>BotApiTests</c> and
/// <c>PresenceBotTests</c> cover the stream itself.</para>
///
/// <para>Every test seeds its own bot through the developer console. The gateway is keyed by the bot
/// and remembers its open streams, consumers and cursor, so sharing one would make each test depend
/// on what the last one left behind.</para>
/// </remarks>
[TestFixture]
public class BotGatewayTests : TestBase
{
    private TestUserSession developer = null!;
    private TeamDetails     team      = null!;
    private PresenceProbe   probe     = null!;

    /// <summary>
    /// The space owner of the current test. Per test rather than per fixture, because an account owns
    /// at most ten spaces and this fixture needs more than that.
    /// </summary>
    private TestUserSession admin = null!;

    private INatsJSContext Js => FactoryAsp.Services.GetRequiredService<INatsJSContext>();

    [OneTimeSetUp]
    public async Task PrepareAsync()
    {
        developer = await CreateSessionAsync();
        team      = await CreateTeamAsync(developer, "gateway");
        probe     = await PresenceProbe.CreateAsync();
    }

    [SetUp]
    public async Task NewSpaceOwnerAsync()
        => admin = await CreateSessionAsync();

    /// <summary>
    /// A bot in no space connects to its own direct events only; installing it starts the space's events
    /// flowing, and each event carries an id the bot can resume from.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_bot_in_no_space_hears_a_space_from_the_moment_it_is_installed(CancellationToken ct = default)
    {
        var bot     = await CreatePublishedBotAsync(developer, team.teamId, "lonely");
        var gateway = Gateway(bot);

        var spaces = await gateway.ConnectAsync(BotIntent.AllNonPrivileged);

        Assert.Multiple(async () =>
        {
            Assert.That(spaces, Is.Empty);
            Assert.That(await gateway.IsConnectedAsync(), Is.True);
            Assert.That(await gateway.GetCursor(), Is.EqualTo("0"));
            Assert.That(await gateway.ConsumeEventsAsync(10), Is.Empty);
        });

        var (spaceId, channelId) = await CreateSpaceWithChannelAsync(admin, "Installed live", ct);
        await InstallAsync(admin, spaceId, bot.appId, ct);

        var installed = await DrainUntilAsync(gateway, e => e.Type == BotEventType.BotInstallingToSpace, ct);

        await SendAsync(spaceId, channelId, "hello, bot", ct);

        var message = await DrainUntilAsync(gateway, e => e.Type == BotEventType.MessageCreate, ct);
        var cursor  = await gateway.GetCursor();

        Assert.Multiple(() =>
        {
            Assert.That(installed.Id, Does.Match("^direct_[1-9][0-9]*$"));
            Assert.That(message.Id, Does.Match($"^{spaceId:N}_[1-9][0-9]*$"));
            Assert.That(cursor, Is.EqualTo($"{spaceId:N}:{message.Id[(message.Id.IndexOf('_') + 1)..]}"),
                "the cursor does not name the last event consumed from the space");
        });

        await gateway.DisconnectAsync();
        await SendAsync(spaceId, channelId, "nobody is listening", ct);

        Assert.Multiple(async () =>
        {
            Assert.That(await gateway.IsConnectedAsync(), Is.False);
            Assert.That(await gateway.ConsumeEventsAsync(10), Is.Empty, "a disconnected gateway still handed out events");
        });
    }

    /// <summary>
    /// Subscribing to a space it already hears, or while it is offline, changes nothing — and no event
    /// arrives twice.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Subscribing_twice_or_while_offline_changes_nothing(CancellationToken ct = default)
    {
        var bot     = await CreatePublishedBotAsync(developer, team.teamId, "twice");
        var gateway = Gateway(bot);

        var (spaceId, channelId) = await CreateSpaceWithChannelAsync(admin, "Subscribed twice", ct);
        await InstallAsync(admin, spaceId, bot.appId, ct);

        await gateway.SubscribeToSpace(spaceId);
        await gateway.UnsubscribeFromSpace(spaceId);

        Assert.That(await gateway.IsConnectedAsync(), Is.False, "subscribing an offline gateway brought it online");

        var spaces = await gateway.ConnectAsync(BotIntent.Messages);
        await gateway.SubscribeToSpace(spaceId);

        await SendAsync(spaceId, channelId, "once", ct);

        await DrainUntilAsync(gateway, e => e.Type == BotEventType.MessageCreate, ct);
        var extra = await DrainForAsync(gateway, TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(spaces.Select(s => s.SpaceId), Is.EqualTo(new[] { spaceId }));
            Assert.That(spaces.Single().PendingApproval, Is.False);
            Assert.That(extra.Where(e => e.Type == BotEventType.MessageCreate), Is.Empty,
                "a second subscription to the same space delivered the message twice");
        });

        await gateway.DisconnectAsync();
    }

    /// <summary>
    /// Uninstalling a bot while it is connected stops that space's events at once and leaves the
    /// others flowing.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Uninstalling_a_connected_bot_stops_only_that_spaces_events(CancellationToken ct = default)
    {
        var bot     = await CreatePublishedBotAsync(developer, team.teamId, "leave");
        var gateway = Gateway(bot);

        var (leaving, leavingChannel) = await CreateSpaceWithChannelAsync(admin, "Left behind", ct);
        var (staying, stayingChannel) = await CreateSpaceWithChannelAsync(admin, "Still here", ct);
        await InstallAsync(admin, leaving, bot.appId, ct);
        await InstallAsync(admin, staying, bot.appId, ct);

        await gateway.ConnectAsync(BotIntent.AllNonPrivileged);

        await SendAsync(leaving, leavingChannel, "before", ct);
        await DrainUntilAsync(gateway, e => e.Type == BotEventType.MessageCreate && e.SpaceId == leaving, ct);

        var result = await Directory(admin).UninstallBot(leaving, bot.appId, ct);
        Assert.That(result, Is.InstanceOf<SuccessUninstallBot>());

        var farewell = await DrainUntilAsync(gateway, e => e.Type == BotEventType.BotUninstallingFromSpace, ct);

        await SendAsync(leaving, leavingChannel, "after", ct);
        await SendAsync(staying, stayingChannel, "still here", ct);

        var delivered = await DrainUntilAsync(gateway, e => e.Type == BotEventType.MessageCreate && e.SpaceId == staying, ct);
        var trailing  = await DrainForAsync(gateway, TimeSpan.FromSeconds(1));
        var cursor    = await gateway.GetCursor();

        Assert.Multiple(async () =>
        {
            Assert.That(farewell.Id, Does.StartWith("direct_"));
            Assert.That(delivered.SpaceId, Is.EqualTo(staying));
            Assert.That(trailing.Where(e => e.SpaceId == leaving), Is.Empty, "the uninstalled space's events still reach the bot");
            Assert.That(cursor, Does.Not.Contain($"{leaving:N}"), "the cursor still names the uninstalled space");
            Assert.That(await ConsumerExistsAsync(NatsStreamExtensions.ToBotEventSubject(leaving), $"bot_{bot.appId:N}_{leaving:N}"),
                Is.False, "the uninstalled space's durable consumer was left behind");
        });

        await gateway.DisconnectAsync();
    }

    /// <summary>
    /// A batch smaller than what is waiting takes what fits and leaves the rest for the next call —
    /// nothing is skipped and nothing is handed out twice.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_small_batch_leaves_the_rest_for_the_next_call(CancellationToken ct = default)
    {
        var bot     = await CreatePublishedBotAsync(developer, team.teamId, "batch");
        var gateway = Gateway(bot);

        var (first, firstChannel)   = await CreateSpaceWithChannelAsync(admin, "Batch one", ct);
        var (second, secondChannel) = await CreateSpaceWithChannelAsync(admin, "Batch two", ct);
        await InstallAsync(admin, first, bot.appId, ct);
        await InstallAsync(admin, second, bot.appId, ct);

        await gateway.ConnectAsync(BotIntent.Messages);

        await SendAsync(first, firstChannel, "one", ct);
        await SendAsync(second, secondChannel, "two", ct);

        // Both have to be waiting before the first small fetch, or the test would be about publish
        // latency rather than about the batch limit.
        Assert.That(await Poll.UntilAsync(async () =>
                await PendingAsync(first, bot.appId) > 0 && await PendingAsync(second, bot.appId) > 0,
            TimeSpan.FromSeconds(15), ct: ct), Is.True, "the two messages never reached the bot's consumers");

        var calls = new List<List<BotSseEvent>>();

        for (var i = 0; i < 6 && calls.Sum(c => c.Count(e => e.Type == BotEventType.MessageCreate)) < 2; i++)
            calls.Add(await gateway.ConsumeEventsAsync(1));

        var messages = calls.SelectMany(c => c).Where(e => e.Type == BotEventType.MessageCreate).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(calls, Has.All.Count.LessThanOrEqualTo(1), "a batch of one returned more than one event");
            Assert.That(messages.Select(m => m.SpaceId), Is.EquivalentTo(new Guid?[] { first, second }));
            Assert.That(messages.Select(m => m.Id).Distinct().Count(), Is.EqualTo(2));
        });

        await gateway.DisconnectAsync();
    }

    /// <summary>
    /// A message the gateway cannot read is acknowledged and skipped: it neither reaches the bot nor
    /// holds back the events behind it, on either stream.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task An_unreadable_event_is_skipped_and_not_redelivered(CancellationToken ct = default)
    {
        var bot     = await CreatePublishedBotAsync(developer, team.teamId, "poison");
        var gateway = Gateway(bot);

        var (spaceId, channelId) = await CreateSpaceWithChannelAsync(admin, "Poisoned", ct);
        await InstallAsync(admin, spaceId, bot.appId, ct);

        await gateway.ConnectAsync(BotIntent.Messages);

        await Js.PublishAsync(NatsStreamExtensions.ToBotEventSubject(spaceId), "{not json"u8.ToArray(), cancellationToken: ct);
        await Js.PublishAsync(NatsStreamExtensions.ToBotEventSubject(spaceId), "null"u8.ToArray(), cancellationToken: ct);
        await Js.PublishAsync(NatsStreamExtensions.ToBotDirectSubject(bot.appId), "{not json"u8.ToArray(), cancellationToken: ct);
        await Js.PublishAsync(NatsStreamExtensions.ToBotDirectSubject(bot.appId), "null"u8.ToArray(), cancellationToken: ct);
        await PublishDirectAsync(bot.appId, BotEventType.BotEntitlementsUpdated, ct);
        await SendAsync(spaceId, channelId, "behind the poison", ct);

        var seen = new List<BotSseEvent>();
        await DrainUntilAsync(gateway, e => e.Type == BotEventType.MessageCreate, ct, seen);

        if (seen.All(e => e.Type != BotEventType.BotEntitlementsUpdated))
            await DrainUntilAsync(gateway, e => e.Type == BotEventType.BotEntitlementsUpdated, ct, seen);

        var afterwards = await DrainForAsync(gateway, TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(seen, Has.None.Matches<BotSseEvent>(e => e is null || e.Data is null));
            Assert.That(afterwards, Is.Empty, "an unreadable message was handed out again");
        });

        await gateway.DisconnectAsync();
    }

    /// <summary>
    /// Events outside the intents a bot connected with are dropped from its direct stream as well as
    /// from its spaces; lifecycle events, which need no intent, always arrive.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Direct_events_outside_the_bots_intents_are_dropped(CancellationToken ct = default)
    {
        var bot     = await CreatePublishedBotAsync(developer, team.teamId, "intent");
        var gateway = Gateway(bot);

        await gateway.ConnectAsync(BotIntent.Messages);

        await PublishDirectAsync(bot.appId, BotEventType.CallIncoming, ct);
        await PublishDirectAsync(bot.appId, BotEventType.BotEntitlementsUpdated, ct);

        var seen = new List<BotSseEvent>();
        await DrainUntilAsync(gateway, e => e.Type == BotEventType.BotEntitlementsUpdated, ct, seen);

        Assert.That(seen.Select(e => e.Type), Has.No.Member(BotEventType.CallIncoming),
            "a call reached a bot that did not ask for calls");

        await gateway.DisconnectAsync();

        await gateway.ConnectAsync(BotIntent.Messages | BotIntent.Calls);
        await PublishDirectAsync(bot.appId, BotEventType.CallIncoming, ct);

        var call = await DrainUntilAsync(gateway, e => e.Type == BotEventType.CallIncoming, ct);
        Assert.That(call.Id, Does.StartWith("direct_"));

        await gateway.DisconnectAsync();
    }

    /// <summary>
    /// A consumer the gateway could not create, or that disappeared under it, costs the bot that
    /// stream — not its connection, and not the uninstall that follows.
    /// </summary>
    /// <remarks>
    /// A durable consumer of the same name with a different delivery policy is something NATS refuses
    /// to update, which is the simplest way to make creation fail for real. Streams vanishing under a
    /// connected gateway is what a NATS restart does to these, which are held in memory.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_missing_consumer_costs_the_bot_that_stream_and_nothing_else(CancellationToken ct = default)
    {
        var blocked = await CreatePublishedBotAsync(developer, team.teamId, "blocked");
        var (blockedSpace, _) = await CreateSpaceWithChannelAsync(admin, "Blocked", ct);
        await InstallAsync(admin, blockedSpace, blocked.appId, ct);

        await SquatConsumerAsync(NatsStreamExtensions.ToBotEventSubject(blockedSpace), $"bot_{blocked.appId:N}_{blockedSpace:N}", ct);
        await SquatConsumerAsync(NatsStreamExtensions.ToBotDirectSubject(blocked.appId), $"bot_{blocked.appId:N}_direct", ct);

        var spaces = await Gateway(blocked).ConnectAsync(BotIntent.AllNonPrivileged);

        Assert.Multiple(async () =>
        {
            Assert.That(spaces.Select(s => s.SpaceId), Is.EqualTo(new[] { blockedSpace }));
            Assert.That(await Gateway(blocked).IsConnectedAsync(), Is.True, "a bot whose consumers failed was left offline");
            Assert.That(await Gateway(blocked).ConsumeEventsAsync(10), Is.Empty);
        });

        await Gateway(blocked).DisconnectAsync();

        var vanished = await CreatePublishedBotAsync(developer, team.teamId, "vanish");
        var (space, _) = await CreateSpaceWithChannelAsync(admin, "Vanished", ct);
        await InstallAsync(admin, space, vanished.appId, ct);

        await Gateway(vanished).ConnectAsync(BotIntent.AllNonPrivileged);

        await Js.DeleteStreamAsync(NatsStreamExtensions.ToBotEventSubject(space), ct);
        await Js.DeleteStreamAsync(NatsStreamExtensions.ToBotDirectSubject(vanished.appId), ct);

        var consumed    = await Gateway(vanished).ConsumeEventsAsync(10);
        var uninstalled = await Directory(admin).UninstallBot(space, vanished.appId, ct);
        var members     = await admin.Servers.GetMembers(space, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(consumed, Is.Empty);
            Assert.That(uninstalled, Is.InstanceOf<SuccessUninstallBot>(), "the uninstall failed on a stream that was already gone");
            Assert.That(members.Values.Select(m => m.member.userId), Has.No.Member(vanished.appId));
            Assert.That(await Gateway(vanished).IsConnectedAsync(), Is.True);
        });

        await Gateway(vanished).DisconnectAsync();
    }

    /// <summary>
    /// A stream that already exists with settings the gateway would not have chosen is used as it is.
    /// </summary>
    /// <remarks>
    /// JetStream refuses to move a stream between storage types, so a stream another deployment
    /// declared on disk makes the gateway's own declaration fail; the consumer on top of it is what
    /// matters, and that still works.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_stream_declared_differently_is_used_as_it_is(CancellationToken ct = default)
    {
        var bot = await CreatePublishedBotAsync(developer, team.teamId, "disk");
        var (spaceId, channelId) = await CreateSpaceWithChannelAsync(admin, "On disk", ct);
        await InstallAsync(admin, spaceId, bot.appId, ct);

        await RedeclareOnDiskAsync(NatsStreamExtensions.ToBotEventSubject(spaceId), ct);
        await RedeclareOnDiskAsync(NatsStreamExtensions.ToBotDirectSubject(bot.appId), ct);

        var gateway = Gateway(bot);
        await gateway.ConnectAsync(BotIntent.Messages);

        await SendAsync(spaceId, channelId, "from disk", ct);
        await PublishDirectAsync(bot.appId, BotEventType.BotEntitlementsUpdated, ct);

        var seen = new List<BotSseEvent>();
        await DrainUntilAsync(gateway, e => e.Type == BotEventType.MessageCreate, ct, seen);

        if (seen.All(e => e.Type != BotEventType.BotEntitlementsUpdated))
            await DrainUntilAsync(gateway, e => e.Type == BotEventType.BotEntitlementsUpdated, ct, seen);

        await gateway.DisconnectAsync();
    }

    /// <summary>
    /// A bot whose entitlements have outgrown what a space approved is told so when it connects.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_bot_connecting_with_unapproved_entitlements_is_told_so(CancellationToken ct = default)
    {
        var bot     = await CreatePublishedBotAsync(developer, team.teamId, "pending");
        var gateway = Gateway(bot);

        var (spaceId, _) = await CreateSpaceWithChannelAsync(admin, "Pending approval", ct);
        await InstallAsync(admin, spaceId, bot.appId, ct);

        var approved = bot.botDetails!.requiredEntitlements;
        var raised   = approved | (ulong)ArgonEntitlement.ManageChannels;

        await As(developer, c => c.Apps.UpdateBotEntitlements(team.teamId, bot.appId, raised, ct).Ok());

        var pending = (await gateway.ConnectAsync(BotIntent.Messages)).Single();
        var told    = await DrainUntilAsync(gateway, e => e.Type == BotEventType.BotEntitlementsUpdated, ct);
        await gateway.DisconnectAsync();

        Assert.That(await Directory(admin).ApproveBotEntitlements(spaceId, bot.appId, ct), Is.InstanceOf<SuccessApproval>());

        var settled = (await gateway.ConnectAsync(BotIntent.Messages)).Single();
        await gateway.DisconnectAsync();

        Assert.Multiple(() =>
        {
            Assert.That(pending.SpaceId, Is.EqualTo(spaceId));
            Assert.That(pending.PendingApproval, Is.True, "the bot was not told its space has not approved its new entitlements");
            Assert.That((ulong)pending.GrantedEntitlements, Is.EqualTo(approved), "the bot was told it holds what it only asked for");
            Assert.That(told.Id, Does.StartWith("direct_"));
            Assert.That(settled.PendingApproval, Is.False);
            Assert.That((ulong)settled.GrantedEntitlements, Is.EqualTo(raised));
        });
    }

    /// <summary>
    /// Closing one of two open streams leaves the bot connected and online; closing the last takes it
    /// offline.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Closing_one_of_two_streams_keeps_the_bot_online(CancellationToken ct = default)
    {
        var bot     = await CreatePublishedBotAsync(developer, team.teamId, "pair");
        var gateway = Gateway(bot);

        var (spaceId, _) = await CreateSpaceWithChannelAsync(admin, "Two streams", ct);
        await InstallAsync(admin, spaceId, bot.appId, ct);

        await gateway.ConnectAsync(BotIntent.Messages);
        await gateway.ConnectAsync(BotIntent.Messages);
        await gateway.DisconnectAsync();

        var stillConnected = await gateway.IsConnectedAsync();
        var stillOnline    = await probe.IsUserOnlineAsync(bot.appId, ct);

        await gateway.DisconnectAsync();
        await gateway.DisconnectAsync();

        var connected = await gateway.IsConnectedAsync();
        var online    = await probe.IsUserOnlineAsync(bot.appId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stillConnected, Is.True, "closing one stream disconnected the other");
            Assert.That(stillOnline, Is.True, "closing one stream took the bot offline while another was open");
            Assert.That(connected, Is.False);
            Assert.That(online, Is.False, "closing the last stream left the bot online");
        });

        await gateway.ConnectAsync(BotIntent.Messages);
        Assert.That(await gateway.IsConnectedAsync(), Is.True, "an extra disconnect left the next connect one short of online");
        await gateway.DisconnectAsync();
    }

    /// <summary>
    /// A connected bot stays online for as long as it listens, however short the session TTL the
    /// deployment runs with.
    /// </summary>
    /// <remarks>
    /// The gateway writes the bot's presence key with <c>Presence:SessionTtl</c> and renews it from a
    /// tick of its own, so the tick has to fit inside the TTL — the same rule
    /// <c>PresenceTimingOptions.Validate</c> enforces for a person's session. This host runs a twelve
    /// second TTL, so a gateway that ticks on a fixed thirty seconds lets its bot lapse to offline
    /// between two ticks while its stream is open.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_connected_bot_stays_online_past_the_session_ttl(CancellationToken ct = default)
    {
        var bot     = await CreatePublishedBotAsync(developer, team.teamId, "linger");
        var gateway = Gateway(bot);

        var (spaceId, _) = await CreateSpaceWithChannelAsync(admin, "Lingering", ct);
        await InstallAsync(admin, spaceId, bot.appId, ct);

        await gateway.ConnectAsync(BotIntent.Messages);

        Assert.That(await probe.IsUserOnlineAsync(bot.appId, ct), Is.True, "premise: a connected bot is online");

        await Task.Delay(probe.Timings.SessionTtl + probe.Timings.RefreshPeriod * 2, ct);

        var online    = await probe.IsUserOnlineAsync(bot.appId, ct);
        var aggregate = await probe.AggregatedStatusAsync(bot.appId, ct);

        await gateway.DisconnectAsync();

        Assert.Multiple(() =>
        {
            Assert.That(online, Is.True, "a bot with an open stream lapsed offline after one session TTL");
            Assert.That(aggregate, Is.EqualTo(UserStatus.Online));
        });
    }

    /// <summary>
    /// A gateway torn down while its stream is open takes the bot offline, as the stream closing would.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_gateway_torn_down_while_connected_takes_the_bot_offline(CancellationToken ct = default)
    {
        var bot     = await CreatePublishedBotAsync(developer, team.teamId, "teardown");
        var gateway = Gateway(bot);

        var (spaceId, _) = await CreateSpaceWithChannelAsync(admin, "Torn down", ct);
        await InstallAsync(admin, spaceId, bot.appId, ct);

        await gateway.ConnectAsync(BotIntent.Messages);
        await gateway.ConnectAsync(BotIntent.Messages);

        Assert.That(await probe.IsUserOnlineAsync(bot.appId, ct), Is.True, "premise: a connected bot is online");

        await GetGrainFactory().GetGrain<IGrainManagementExtension>(gateway.GetGrainId()).DeactivateOnIdle();

        var offline = await Poll.UntilAsync(async () => !await probe.IsUserOnlineAsync(bot.appId, ct),
            TimeSpan.FromSeconds(15), ct: ct);

        Assert.Multiple(async () =>
        {
            Assert.That(offline, Is.True, "a bot whose gateway was torn down is still online");
            Assert.That(await gateway.IsConnectedAsync(), Is.False);
        });
    }

    /// <summary>
    /// A gateway keyed by an account that is not a bot reports its spaces with nothing granted and
    /// nothing pending, rather than inventing entitlements.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_gateway_for_an_account_that_is_no_bot_grants_nothing(CancellationToken ct = default)
    {
        var person = await CreateSessionAsync(ct);
        var (spaceId, _) = await CreateSpaceWithChannelAsync(person, "Not a bot", ct);

        var gateway = GetGrainFactory().GetGrain<IBotGatewayGrain>(person.UserId);
        var spaces  = await gateway.ConnectAsync(BotIntent.Messages);
        await gateway.DisconnectAsync();

        Assert.That(spaces, Is.EqualTo(new[] { new BotSpaceInfo(spaceId, default, false) }));
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private IBotGatewayGrain Gateway(AppDetails bot)
        => GetGrainFactory().GetGrain<IBotGatewayGrain>(bot.appId);

    private IBotManagementInteraction Directory(TestUserSession who)
        => who.Client.ForService<IBotManagementInteraction>(FactoryAsp.Services);

    private Task SendAsync(Guid spaceId, Guid channelId, string text, CancellationToken ct)
        => admin.Channels.SendMessage(spaceId, channelId, text, new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct).Ok();

    /// <summary>
    /// Consumes until an event matching <paramref name="match"/> arrives, and fails the test if none
    /// does in time. Everything consumed on the way is added to <paramref name="seen"/>.
    /// </summary>
    private static async Task<BotSseEvent> DrainUntilAsync(
        IBotGatewayGrain gateway, Func<BotSseEvent, bool> match, CancellationToken ct, List<BotSseEvent>? seen = null)
    {
        var found = await DrainOrNullAsync(gateway, match, TimeSpan.FromSeconds(20), seen);

        Assert.That(found, Is.Not.Null,
            $"the bot never received the event it was waiting for; it got [{string.Join(", ", seen?.Select(e => e.Type) ?? [])}]");

        return found!;
    }

    private static async Task<BotSseEvent?> DrainOrNullAsync(
        IBotGatewayGrain gateway, Func<BotSseEvent, bool> match, TimeSpan timeout, List<BotSseEvent>? seen = null)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            // The whole batch is recorded before looking for the match: an event that arrived in the
            // same call as the one waited for has been acknowledged and will not come again.
            var batch = await gateway.ConsumeEventsAsync(50);
            seen?.AddRange(batch);

            if (batch.FirstOrDefault(match) is { } found)
                return found;

            await Task.Delay(100);
        }

        return null;
    }

    /// <summary>Everything the gateway hands out over <paramref name="window"/>.</summary>
    private static async Task<List<BotSseEvent>> DrainForAsync(IBotGatewayGrain gateway, TimeSpan window)
    {
        var events   = new List<BotSseEvent>();
        var deadline = DateTime.UtcNow + window;

        while (DateTime.UtcNow < deadline)
        {
            events.AddRange(await gateway.ConsumeEventsAsync(50));
            await Task.Delay(100);
        }

        return events;
    }

    /// <summary>A direct event of the given type, published the way <c>BotEventPublisher</c> publishes one.</summary>
    private async Task PublishDirectAsync(Guid botUserId, BotEventType type, CancellationToken ct)
    {
        var evt = new BotSseEvent
        {
            Id   = "pending",
            Type = type,
            Data = new Dictionary<string, object> { ["callId"] = Guid.NewGuid() }
        };

        await Js.PublishAsync(NatsStreamExtensions.ToBotDirectSubject(botUserId), evt,
            serializer: new BotSseEventSerializer(), cancellationToken: ct);
    }

    /// <summary>How many messages the bot's consumer for a space has yet to hand out.</summary>
    private async Task<ulong> PendingAsync(Guid spaceId, Guid botUserId)
    {
        var consumer = await Js.GetConsumerAsync(NatsStreamExtensions.ToBotEventSubject(spaceId), $"bot_{botUserId:N}_{spaceId:N}");
        return consumer.Info.NumPending;
    }

    private async Task<bool> ConsumerExistsAsync(string stream, string consumer)
    {
        try
        {
            await Js.GetConsumerAsync(stream, consumer);
            return true;
        }
        catch (NatsJSApiException e) when (e.Error.Code == 404)
        {
            return false;
        }
    }

    /// <summary>The stream, deleted and declared again on disk rather than in memory.</summary>
    private async Task RedeclareOnDiskAsync(string stream, CancellationToken ct)
    {
        await Js.DeleteStreamAsync(stream, ct);
        await Js.CreateStreamAsync(new StreamConfig(stream, [stream])
        {
            MaxAge  = TimeSpan.FromMinutes(5),
            Storage = StreamConfigStorage.File
        }, ct);
    }

    /// <summary>
    /// Takes a durable consumer name before the gateway can, with a delivery policy NATS will not let
    /// the gateway's own declaration change.
    /// </summary>
    private async Task SquatConsumerAsync(string stream, string consumer, CancellationToken ct)
    {
        await Js.CreateOrUpdateStreamAsync(new StreamConfig(stream, [stream])
        {
            DuplicateWindow = TimeSpan.Zero,
            MaxAge          = TimeSpan.FromMinutes(5),
            AllowDirect     = true,
            MaxBytes        = -1,
            Retention       = StreamConfigRetention.Limits,
            Storage         = StreamConfigStorage.Memory,
            Discard         = StreamConfigDiscard.Old
        }, ct);

        await Js.CreateOrUpdateConsumerAsync(stream, new ConsumerConfig(consumer)
        {
            AckPolicy     = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.All
        }, ct);
    }
}
