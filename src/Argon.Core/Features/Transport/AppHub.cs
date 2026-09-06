namespace Argon.Core.Features.Transport;

using Argon.Features.Auth;
using Argon.Features.BotApi;
using Argon.Features.Clustering;
using Argon.Features.Env;
using Argon.Services;
using ion.runtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using StackExchange.Redis;
using System.Formats.Cbor;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;

[Authorize(AuthenticationSchemes = "Ticket", Policy = "ticket")]
public class AppHub(
    IGrainFactory factory,
    IRealtimeReplayBuffer replay,
    HybridCache cache,
    IArgonCacheDatabase cacheDb) : Hub
{
    public async override Task OnConnectedAsync()
    {
        EnsureBeforeCall(true);

        // A ticket is minted once and an established socket is never re-authenticated, so a device
        // that was signed out an hour ago can still open a brand new connection with the ticket it
        // already holds. Checked before any group is joined or the session grain is touched (S6).
        if (await IsSessionRevokedAsync())
        {
            Context.Abort();
            return;
        }

        var spaceIds = await factory.GetGrain<IUserGrain>(UserId).GetMyServersIds();
        await Task.WhenAll(spaceIds.Select(x => Groups.AddToGroupAsync(Context.ConnectionId, $"spaces/{x}")));
        // Session grain is keyed by the stable sid (not this ephemeral ConnectionId); a reconnect of the
        // same client re-attaches to the same session instead of churning a fresh one.
        await factory.GetGrain<IUserSessionGrain>(SessionGrainKey).AttachConnectionAsync(Context.ConnectionId);
    }

    /// <summary>
    /// Replay events the client missed while it was briefly disconnected.
    ///
    /// The client passes the last entry id it saw on its personal (<c>forSelf</c>) stream and
    /// on each subscribed space (<c>broadcastSpace</c>) stream. We re-send everything after
    /// those cursors through the normal client handlers (the client dedupes by id). If any
    /// cursor is too old to guarantee continuity we set <see cref="ResumeAck.NeedFullResync"/>
    /// so the client reloads its state from scratch instead of trusting a partial replay.
    /// </summary>
    public async Task<ResumeAck> Resume(string? userCursor, Dictionary<string, string>? spaceCursors)
    {
        EnsureBeforeCall(true);
        await EnsureSessionIsLiveAsync();

        var needFullResync = false;

        var userResult = await replay.ReadUserSinceAsync(UserId, userCursor);
        if (userResult.Gap)
            needFullResync = true;
        foreach (var e in userResult.Entries)
            await Clients.Caller.SendAsync("forSelf", e.Payload, e.Id);

        if (spaceCursors is { Count: > 0 })
        {
            // Only replay spaces the user is still a member of — membership may have changed
            // during the gap, and we must not leak events from spaces they no longer belong to.
            var mySpaces = (await factory.GetGrain<IUserGrain>(UserId).GetMyServersIds()).ToHashSet();

            foreach (var (spaceIdRaw, cursor) in spaceCursors)
            {
                if (!Guid.TryParse(spaceIdRaw, out var spaceId) || !mySpaces.Contains(spaceId))
                    continue;

                var spaceResult = await replay.ReadSpaceSinceAsync(spaceId, cursor);
                if (spaceResult.Gap)
                {
                    needFullResync = true;
                    continue;
                }

                foreach (var e in spaceResult.Entries)
                    await Clients.Caller.SendAsync("broadcastSpace", e.Payload, spaceId, e.Id);
            }
        }

        return new ResumeAck(needFullResync);
    }

    private Guid UserId => Guid.Parse(Context.UserIdentifier!);

    // Stable session-grain key "{userId}:{sid}". sid is the per-launch ticket claim, so reconnects of
    // the same client resolve to the same session grain.
    private string SessionGrainKey => $"{UserId}:{Context.User!.FindFirstValue("sid")}";

    /// <summary>
    /// How long a revocation may take to be honoured on this path.
    /// </summary>
    /// <remarks>
    /// The same numbers <c>ArgonTransactionInterceptor</c> uses for the same key, so the Ion path and
    /// the hub cannot disagree about when a sign-out takes effect. Cached at all because the answer
    /// is "no" for essentially every call ever made, and a heartbeat every fifteen seconds per
    /// connection is not worth one Redis round trip each.
    /// </remarks>
    private static readonly HybridCacheEntryOptions RevokedSessionCacheOptions = new()
    {
        Expiration           = TimeSpan.FromSeconds(15),
        LocalCacheExpiration = TimeSpan.FromSeconds(5),
    };

    /// <summary>Whether the sid this connection authenticated with has been signed out.</summary>
    /// <remarks>
    /// <para>Defect S6. The ticket carries a signed <c>sid</c> and is good for as long as it lives;
    /// nothing on an established SignalR connection ever looks at it again, and no hub method
    /// consulted <c>SessionRevocation.RevokedKey</c>. So a revoked device kept receiving every
    /// broadcast and its next <c>Heartbeat</c> — fifteen seconds away — re-created the session's
    /// presence keys and put the row back on the devices screen.</para>
    ///
    /// <para>Deliberately a copy of the read in <c>ArgonTransactionInterceptor.IsSessionRevokedAsync</c>
    /// rather than a call to it: that one is a private static on the interceptor and reaching it from
    /// here would mean exporting it, which is a change to a file this work does not own. Same key,
    /// same cache entry, same options, same legacy fallback and the same <em>fail-open</em> — a store
    /// incident must not disconnect the whole instance. The load-bearing check is in
    /// <c>UserSessionGrain</c>, which reads the set uncached on a session start; this one is what
    /// stops the traffic.</para>
    /// </remarks>
    private async Task<bool> IsSessionRevokedAsync()
    {
        if (!Guid.TryParse(Context.User?.FindFirstValue("sid"), out var sid))
            return false;
        if (!Guid.TryParse(Context.UserIdentifier, out var userId))
            return false;

        var key = SessionRevocation.RevokedKey(userId);

        try
        {
            // The whole set per user, not one entry per (user, session) pair: it is a handful of ids.
            var revoked = await cache.GetOrCreateAsync(
                key,
                async token => await cacheDb.SetMembersAsync(key, token),
                RevokedSessionCacheOptions);

            if (revoked.Contains(sid.ToString()))
                return true;

            var legacy = SessionRevocation.LegacyRevokedKey(userId, sid);

            return await cache.GetOrCreateAsync(
                legacy,
                async token => await cacheDb.KeyExistsAsync(legacy, token),
                RevokedSessionCacheOptions);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Refuses the call, and the connection, when this session has been signed out.</summary>
    private async Task EnsureSessionIsLiveAsync()
    {
        if (!await IsSessionRevokedAsync())
            return;

        // Both, because they answer different halves: the abort stops a silent connection that makes
        // no further calls from receiving anything, the exception tells the caller why this one failed.
        Context.Abort();
        throw new HubException("this session has been signed out");
    }

    private void EnsureBeforeCall(bool isAllowAbort = false)
    {
        bool takeClaim(string key, out string value)
        {
            value = "";
            var kv = Context.User?.FindFirst(key);
            if (kv is null && isAllowAbort)
            {
                Context.Abort();
                return false;
            }

            if (kv is null)
                throw new InvalidOperationException($"invalid operations, claim '{key}' is not found in user ticket");
            value = kv.Value;
            return true;
        }


        var userId = Context.UserIdentifier!;
        if (!takeClaim("sid", out var sessionId))
            return;
        if (!takeClaim("mid", out var machineId))
            return;

        RequestContext.AllowCallChainReentrancy();
        this.SetUserId(Guid.Parse(userId));
        this.SetUserMachineId(machineId);
        this.SetUserSessionId(Guid.Parse(sessionId));
    }

    public async override Task OnDisconnectedAsync(Exception? exception)
    {
        // Detach this connection from its session. If it was the last one, the session arms a grace
        // reminder (it does NOT go offline immediately) so a transient drop/reconnect doesn't flap.
        var sid = Context.User?.FindFirstValue("sid");
        if (Context.UserIdentifier is null || string.IsNullOrEmpty(sid))
            return;
        await factory.GetGrain<IUserSessionGrain>($"{Context.UserIdentifier}:{sid}")
           .DetachConnectionAsync(Context.ConnectionId);
    }

    /// <summary>Puts this connection on a space's broadcast group — if the caller is a member of it.</summary>
    /// <remarks>
    /// <para>Defect S11. This was <c>AddToGroupAsync</c> and nothing else, so any authenticated
    /// client that knew a space id could put itself on that space's stream and watch everything
    /// published to it — presence, typing, roster changes, messages. Space ids are not secrets: they
    /// travel through invite previews and shared links. <c>OnConnectedAsync</c> is careful to join
    /// only the caller's own spaces and <c>Resume</c> is careful to replay only spaces they are still
    /// in; this method undid both.</para>
    ///
    /// <para>Gated on the same source of truth those two already use. Refusing outright is safe for
    /// the shipped desktop client, which defines this call and never makes it.</para>
    /// </remarks>
    public async Task SubscribeToSpace(Guid spaceId)
    {
        EnsureBeforeCall(true);
        await EnsureSessionIsLiveAsync();

        if (!(await factory.GetGrain<IUserGrain>(UserId).GetMyServersIds()).Contains(spaceId))
            throw new HubException("not a member of this space");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"spaces/{spaceId}");
    }

    /// <summary>Takes this connection off a space's broadcast group.</summary>
    /// <remarks>
    /// Ungated and idempotent by construction, and both on purpose: leaving a group you are not in is
    /// a no-op in SignalR, and a caller asking to receive <em>less</em> never needs permission — a
    /// membership check here would only be a way for a lost membership to strand a subscription.
    /// </remarks>
    public async Task UnSubscribeToSpace(Guid spaceId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"spaces/{spaceId}");

    /// <summary>
    /// Channel-scoped subscription: the client joins only the channel it currently has open, so
    /// channel content (messages/typing/reactions) is delivered to viewers instead of the whole space.
    /// </summary>
    /// <remarks>
    /// The damaging half of S11 — a channel group carries message content — and the one the desktop
    /// client really calls, so it has to keep succeeding for real members. <c>IChannelGrain</c>
    /// exposes no space accessor, so the channel's owning space and the caller's membership of it are
    /// resolved together by <see cref="IUserGrain.ResolveChannelSpaceIfMemberAsync"/>. This gates on
    /// space membership only; a per-channel entitlement check belongs with the private-channel
    /// routing work and is not folded in here.
    /// </remarks>
    public async Task SubscribeToChannel(Guid channelId)
    {
        EnsureBeforeCall(true);
        await EnsureSessionIsLiveAsync();

        if (await factory.GetGrain<IUserGrain>(UserId).ResolveChannelSpaceIfMemberAsync(channelId) is null)
            throw new HubException("not a member of this channel's space");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"channels/{channelId}");
    }

    /// <inheritdoc cref="UnSubscribeToSpace"/>
    public async Task UnSubscribeToChannel(Guid channelId)
        => await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"channels/{channelId}");

    public async Task Heartbeat(UserStatus status)
    {
        await EnsureSessionIsLiveAsync();

        // The grain answers false when it refuses to (re)start a signed-out session, which is the
        // layer that catches a revocation the cached gate above has not seen yet. Treated exactly
        // like the gate: the call fails and the connection goes.
        if (await factory.GetGrain<IUserSessionGrain>(SessionGrainKey).HeartBeatAsync(Context.ConnectionId, status))
            return;

        Context.Abort();
        throw new HubException("this session has been signed out");
    }

    // Explicit, intentional offline — the client calls this on logout/quit/account-switch so others see
    // them go offline immediately instead of lingering for the disconnect grace window. Scoped to THIS
    // connection: another window of the same session staying open means the user has not gone
    // anywhere, and finalizing the whole session there published an Offline the survivor's next
    // heartbeat immediately undid (S9).
    public async Task GoOffline()
    {
        await EnsureSessionIsLiveAsync();
        await factory.GetGrain<IUserSessionGrain>(SessionGrainKey).GoOfflineAsync(Context.ConnectionId);
    }

    /// <summary>
    /// Typing, addressed by the space the channel belongs to as well as the channel.
    /// </summary>
    /// <remarks>
    /// <para>The space id is here for routing and is deliberately unused today. Once there is a second
    /// region, a call arriving in one region for a channel homed in another has to be sent there, and
    /// the decision needs to know which region owns the thing being addressed. Every other channel
    /// operation already carries the space — <c>ChannelInteraction</c> is declared
    /// <c>service ChannelInteraction(spaceId, channelId)</c> — and these two were the only client-to-
    /// server calls in the product that named a channel and nothing else.</para>
    ///
    /// <para>Deriving the region from the channel id instead would have worked, because
    /// <c>ArgonId.NewIn(spaceId)</c> makes a channel inherit its space's region. It was rejected: that
    /// is correct only while every space-scoped id is minted through <c>NewIn</c>, some are minted with
    /// <c>ArgonId.New()</c> — which stamps the region of whichever process happened to run — and a
    /// routing decision resting on mint discipline fails silently and in production. The space is the
    /// authority, so the space travels.</para>
    ///
    /// <para>Added beside the old ones rather than replacing them, and that ordering is the point: the
    /// client can start sending the space while the server still ignores it, so the routing seam lands
    /// later without a second coordinated release.</para>
    ///
    /// <para>A new NAME rather than an overload, because SignalR refuses one: hub method discovery
    /// throws <c>Duplicate definitions of 'IAmTyping'. Overloading is not supported.</c> at startup, so
    /// the obvious shape takes the whole process down rather than failing at the call.</para>
    /// </remarks>
    public async Task IAmTypingIn(Guid spaceId, Guid channelId)
    {
        EnsureBeforeCall(true);
        await factory.GetGrain<IChannelGrain>(channelId).OnTypingEmit();
    }

    /// <inheritdoc cref="IAmTypingIn"/>
    public async Task IAmStopTypingIn(Guid spaceId, Guid channelId)
    {
        EnsureBeforeCall(true);
        await factory.GetGrain<IChannelGrain>(channelId).OnTypingStopEmit();
    }

    /// <summary>Deprecated: names a channel with no space, so it cannot be routed.</summary>
    /// <remarks>
    /// Kept working for every client that has not moved to the overload above. It is the one shape a
    /// second region cannot serve correctly — the call would be handled wherever it landed rather than
    /// where the channel lives — so it should be removed once the desktop client has shipped the
    /// change, and not before.
    /// </remarks>
    [Obsolete("Send the space id: IAmTypingIn(spaceId, channelId). It cannot be routed across regions.")]
    public async Task IAmTyping(Guid channelId)
    {
        EnsureBeforeCall(true);
        await factory.GetGrain<IChannelGrain>(channelId).OnTypingEmit();
    }

    /// <inheritdoc cref="IAmTyping"/>
    [Obsolete("Send the space id: IAmStopTypingIn(spaceId, channelId). It cannot be routed across regions.")]
    public async Task IAmStopTyping(Guid channelId)
    {
        EnsureBeforeCall(true);
        await factory.GetGrain<IChannelGrain>(channelId).OnTypingStopEmit();
    }
}

/// <summary>Result of <see cref="AppHub.Resume"/>. Serialized to the client over the hub protocol.</summary>
public sealed record ResumeAck(bool NeedFullResync);

public class AppHubServer(
    IHubContext<AppHub> appHub,
    BotEventPublisher botEventPublisher,
    IRealtimeReplayBuffer replay,
    ILogger<AppHubServer> logger)
{
    public async Task BroadcastSpace<T>(T @event, Guid spaceId, CancellationToken ct = default)
        where T : IArgonEvent
    {
        var writer = new CborWriter();
        IonFormatterStorage.GetFormatter<IArgonEvent>().Write(writer, @event);
        var payload = writer.Encode();

        // Persist to the replay log first so the cursor (entry id) we hand the client is
        // durable: if it reconnects it can ask for everything after this id.
        var entryId = await replay.AppendSpaceAsync(spaceId, payload, ct);

        await appHub.Clients.Group($"spaces/{spaceId}")
           .SendAsync("broadcastSpace", payload, spaceId, entryId, cancellationToken: ct);

        // Publish to NATS for bots — single publish, bots consume independently
        _ = botEventPublisher.PublishIfMappedAsync(@event, spaceId);
    }

    /// <summary>
    /// Channel-scoped delivery for high-frequency channel content (messages, typing, reactions).
    /// Only clients currently viewing the channel join its group, so a message fans out to channel
    /// viewers — not to all N members of the space. Missed messages on a brief disconnect are
    /// recovered by the client re-fetching the open channel's recent history on reconnect (messages
    /// are persisted; reactions load with them; typing is ephemeral), so there is no replay stream.
    /// </summary>
    public async Task BroadcastChannel<T>(T @event, Guid spaceId, Guid channelId, CancellationToken ct = default)
        where T : IArgonEvent
    {
        var writer = new CborWriter();
        IonFormatterStorage.GetFormatter<IArgonEvent>().Write(writer, @event);
        var payload = writer.Encode();

        await appHub.Clients.Group($"channels/{channelId}")
           .SendAsync("broadcastChannel", payload, channelId, cancellationToken: ct);

        // Bots are mapped per-space (not per-channel), so channel content still reaches them through
        // the existing space NATS mapping exactly as before.
        _ = botEventPublisher.PublishIfMappedAsync(@event, spaceId);
    }

    public async Task ForUser<T>(T @event, Guid userId, CancellationToken ct = default)
        where T : IArgonEvent
    {
        var writer = new CborWriter();
        IonFormatterStorage.GetFormatter<IArgonEvent>().Write(writer, @event);
        var payload = writer.Encode();

        var entryId = await replay.AppendUserAsync(userId, payload, ct);

        await appHub.Clients.User(userId.ToString())
           .SendAsync("forSelf", payload, entryId, cancellationToken: ct);

        // Publish to NATS for bots (calls, DMs)
        _ = botEventPublisher.PublishForUserAsync(@event, userId);
    }
}

public sealed class GuidUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
        => connection.User?.FindFirstValue(ClaimTypes.NameIdentifier);
}

public static class SignalRHubExtensions
{
    public static IHubContext<AppHub> GetAppHubContext(this IServiceProvider serviceProvider)
        => serviceProvider.GetRequiredService<IHubContext<AppHub>>();

    // No role check here any more: only the role that enables AppHubFeature calls this.
    /// <summary>
    /// The publishing half: SignalR over the Redis backplane, plus <see cref="AppHubServer"/> and the
    /// replay log it writes to.
    /// </summary>
    /// <remarks>
    /// Everything that raises an event needs this, silos included — six grain classes take
    /// <see cref="AppHubServer"/> — and none of them needs the client endpoint. Publishing goes
    /// through <c>IHubContext</c>, which hands the message to the backplane; the node holding the
    /// client's connection is the one that delivers it. Mapping the hub is what accepts connections,
    /// and that is a separate concern with its own feature.
    /// </remarks>
    public static void AddRealtimeBus(this WebApplicationBuilder builder)
    {
        builder.AddBotRuntimeServices();

        builder.Services
           .AddSingleton<IRealtimeReplayBuffer, RedisRealtimeReplayBuffer>()
           .AddSingleton<IUserIdProvider, GuidUserIdProvider>()
           .AddScoped<AppHubServer>()
           .AddSignalR()
           //.AddMessagePackProtocol()
           .AddHubOptions<AppHub>(options => options.EnableDetailedErrors = true)
           .AddStackExchangeRedis(x =>
            {
                x.Configuration               = new RedisProfileRegistry(builder.Configuration).BuildOptions(RedisProfiles.Backplane);
                x.Configuration.ChannelPrefix = new RedisChannel(
                    BackplaneChannelPrefix(builder.Configuration), RedisChannel.PatternMode.Literal);
            });
    }

    /// <summary>
    /// The Redis pub/sub namespace this region's SignalR backplane fans out on.
    /// </summary>
    /// <remarks>
    /// <para>The region is in the name because nothing else keeps one region's backplane out of
    /// another's. A Redis profile is a connection string plus a default database, and the database
    /// buys nothing here — pub/sub is not database-scoped, so two regions whose
    /// <see cref="RedisProfiles.Backplane"/> profiles resolve to the same server are one fan-out
    /// domain however different their database indexes are. That failure looks like success:
    /// cross-region delivery works, data residency is being violated the whole time it does, and
    /// whatever came to rely on it breaks the day the two Redis instances are properly separated.</para>
    ///
    /// <para>Read straight out of <see cref="IConfiguration"/> rather than from bound options,
    /// because this runs while the host is still being built and the options container does not exist
    /// yet. It is the same key <see cref="ArgonRegionOptions"/> binds, so a deployment names its
    /// region once and this follows. With no region section at all the answer is the datacenter the
    /// process already declares, which is what a single-region deployment gets — and it is why
    /// adding the section later, naming the region it was already in, is a no-op here rather than a
    /// rename.</para>
    ///
    /// <para>Changing this value is a breaking deployment change, and there is no compatibility
    /// window to be had: the prefix <em>is</em> the channel namespace, so pods on the old one and
    /// pods on the new one cannot see each other's broadcasts at all. Through a rolling deploy,
    /// clients held by an old pod stop receiving anything raised on a new pod and vice versa —
    /// presence, typing, every space broadcast — until the last old pod is gone. Nothing is lost
    /// from the database and nothing needs draining, but the split is visible to users for the length
    /// of the rollout, so roll it through quickly and not at peak.</para>
    /// </remarks>
    public static string BackplaneChannelPrefix(IConfiguration configuration)
    {
        var region = configuration[$"{ArgonRegionOptions.SectionName}:{nameof(ArgonRegionOptions.Self)}"];

        // Blank is unset. Taken literally it would leave an empty segment in the prefix, which hands
        // every region that got its configuration wrong the same fan-out domain again — the exact
        // thing this is here to prevent.
        if (string.IsNullOrWhiteSpace(region))
            region = ArgonDatacenter.Current;

        // Trailing separator so the region stays its own segment instead of running into the hub name
        // the backplane appends after it.
        return $"argon-bus:{region.Trim()}:";
    }

    /// <summary>
    /// The receiving half: the ticket scheme a client authenticates the socket with, and the policy
    /// the mapped hub requires. Only a role that clients connect to needs it.
    /// </summary>
    public static void AddAppHubEndpoint(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication()
           .AddScheme<AuthenticationSchemeOptions, TicketAuthHandler>("Ticket", _ => { });

        builder.Services.AddAuthorization(o =>
        {
            o.AddPolicy("ticket", policy =>
            {
                policy.AddAuthenticationSchemes("Ticket");
                policy.RequireAuthenticatedUser();
                policy.RequireClaim("typ", "ticket");
            });
        });
    }
}

public sealed class TicketAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments("/w") && !Request.Path.StartsWithSegments("/api/spaces"))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = Request.Query["access_token"].ToString();

        if (string.IsNullOrEmpty(token))
        {
            var          auth   = Request.Headers[HeaderNames.Authorization].ToString();
            const string prefix = "Bearer ";
            if (auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                token = auth[prefix.Length..].Trim();
        }

        if (string.IsNullOrEmpty(token))
            return Task.FromResult(AuthenticateResult.NoResult());

        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(configuration["TicketJwt:Key"]!));

        var tvp = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer    = "ticket.argon.gl",

            ValidateAudience = true,
            ValidAudience    = "ticket.argon.gl",

            ValidateLifetime = true,
            ClockSkew        = TimeSpan.FromSeconds(10),

            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = key,
        };

        try
        {
            var handler   = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, tvp, out var validatedToken);

            // typ=ticket обязательно
            var typ = principal.FindFirst("typ")?.Value;
            if (!string.Equals(typ, "ticket", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.Fail("Not a ticket token"));

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(AuthenticateResult.Fail(ex));
        }
    }
}