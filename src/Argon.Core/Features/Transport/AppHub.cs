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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using StackExchange.Redis;
using System.Formats.Cbor;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;

/// <summary>
/// The client-facing end of the realtime bus: one socket per window, authenticated once by a ticket.
/// </summary>
/// <remarks>
/// <para><b>How a sign-out reaches a socket, in three layers, because no one of them is enough.</b>
/// A ticket is minted once and an established connection is never re-authenticated, so everything
/// below exists to answer the same question after the fact: this device was signed out — does it
/// still have a feed?</para>
///
/// <list type="number">
/// <item><description><b>The gate on the calls.</b> <see cref="OnConnectedAsync"/> and every
/// state-changing method consult <c>SessionRevocation</c> (see <see cref="EnsureSessionIsLiveAsync"/>)
/// and abort on a hit. It is the layer that is always correct and always late: it runs when the
/// <em>client</em> speaks, and a client that has been signed out has no reason to. The shipped
/// desktop beats every fifteen seconds, so fifteen seconds is the best this layer can do — and a
/// silent listener, which is exactly what an attacker holding a copied data folder would run, is
/// never asked at all while every broadcast keeps arriving.</description></item>
///
/// <item><description><b>The signal.</b> <c>SecurityGrain.EndSessionAsync</c> and
/// <c>WebSessionEndpoints.LogoutAsync</c> publish the ended ids on NATS core pub/sub the moment the
/// tombstone commits; <see cref="SessionRevocationSubscriber"/> runs on every node that maps this
/// hub and closes the matching connections through <see cref="HubConnectionRegistry"/>. That is what
/// makes a sign-out immediate rather than eventual. It is fail-soft on purpose — a bus hiccup drops
/// the courtesy, never the revocation, because the tombstone is the truth and this is only its
/// delivery.</description></item>
///
/// <item><description><b>The floor.</b> <see cref="HubConnectionSweeper"/> walks the registry on a
/// timer and re-reads the tombstones and the floor for every live connection, so a lost signal costs
/// one sweep rather than the life of the socket. It depends on nothing but Redis, which is the same
/// store the revocation itself lives in.</description></item>
/// </list>
///
/// <para>The registry is populated here and nowhere else, and only after a successful attach: an
/// entry for a connection the session does not believe in would be a handle to nothing.</para>
/// </remarks>
[Authorize(AuthenticationSchemes = "Ticket", Policy = "ticket")]
public class AppHub(
    IGrainFactory factory,
    IRealtimeReplayBuffer replay,
    HybridCache cache,
    IArgonCacheDatabase cacheDb,
    HubConnectionRegistry registry,
    ILogger<AppHub> logger) : Hub
{
    /// <summary>
    /// Marks a connection that actually reached <c>AttachConnectionAsync</c>.
    /// </summary>
    /// <remarks>
    /// <c>Context.Abort()</c> makes SignalR call <see cref="OnDisconnectedAsync"/>, which used to
    /// detach unconditionally — so a connection refused before it ever attached still activated the
    /// session grain, removed a connection id that was never added, saw the set empty and armed the
    /// durable one-minute grace reminder. A revoked client in a reconnect loop therefore booked one
    /// pointless <c>FinalizeOfflineAsync</c> per minute, forever. Now the detach only answers an
    /// attach. <c>Context.Items</c> is per connection and lives on the node holding it, which is the
    /// same node that runs both callbacks.
    /// </remarks>
    private const string AttachedItem = "argon.attached";

    public async override Task OnConnectedAsync()
    {
        if (!EnsureBeforeCall(true))
            return;

        // A ticket is minted once and an established socket is never re-authenticated, so a device
        // that was signed out an hour ago can still open a brand new connection with the ticket it
        // already holds. Checked before any group is joined or the session grain is touched (S6) —
        // uncached, because a fifteen-second-stale "not revoked" is enough to put a signed-out
        // device back on every space group it used to be on, and a connect happens once.
        if (await IsSessionRevokedAsync(onConnect: true))
        {
            await TellSignedOutAsync();
            Context.Abort();
            return;
        }

        var spaceIds = await factory.GetGrain<IUserGrain>(UserId).GetMyServersIds();
        await Task.WhenAll(spaceIds.Select(x => Groups.AddToGroupAsync(Context.ConnectionId, $"spaces/{x}")));
        // Session grain is keyed by the stable sid (not this ephemeral ConnectionId); a reconnect of the
        // same client re-attaches to the same session instead of churning a fresh one.
        // And the second, uncached line of the same gate: the grain re-reads the tombstones itself, so
        // it catches a sign-out this connection's own check was too old to see. A refusal there used
        // to be invisible here — the socket stayed open on every group joined above until the client's
        // next heartbeat noticed, up to fifteen seconds of a signed-out device receiving everything.
        if (!await factory.GetGrain<IUserSessionGrain>(SessionGrainKey).AttachConnectionAsync(Context.ConnectionId))
        {
            await TellSignedOutAsync();
            Context.Abort();
            return;
        }

        Context.Items[AttachedItem] = true;

        // The handle the other two layers need. After the attach, deliberately: a registry entry is a
        // promise that this connection is one the session is counting, and the two must not disagree.
        // Both of the ids on the ticket go in — see HubConnectionEntry — because a device that
        // rotated its scid is only reachable through the credential ones.
        registry.Attach(new HubConnectionEntry(
            Context.ConnectionId, UserId, Guid.Parse(Context.User!.FindFirstValue("sid")!),
            CredentialSessionIds(), TicketIssuedAt(), Context));
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
        RequireTicket();
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

    /// <summary>Whether the session this connection authenticated as has been signed out.</summary>
    /// <remarks>
    /// <para>Defect S6. The ticket carries a signed <c>sid</c> and is good for as long as it lives;
    /// nothing on an established SignalR connection ever looks at it again, and no hub method
    /// consulted <c>SessionRevocation.RevokedKey</c>. So a revoked device kept receiving every
    /// broadcast and its next <c>Heartbeat</c> — fifteen seconds away — re-created the session's
    /// presence keys and put the row back on the devices screen.</para>
    ///
    /// <para>The reading itself is <see cref="SessionRevocation.IsRevokedAsync"/> — one gate for the
    /// Ion path, this one and the connection sweep, so the three cannot disagree about when a
    /// sign-out takes effect. What this method contributes is the identity: the ticket's <c>sid</c>
    /// is the presence id and <em>the caller writes it</em>, so every <c>csid</c> claim
    /// <c>EventBusImpl.PickTicket</c> stamped beside it goes in too, and the <c>iat</c> goes in for
    /// the floor.</para>
    ///
    /// <para><b>Where the policy differs from the interceptor's, and why.</b>
    /// <c>ArgonTransactionInterceptor</c> fails <em>open</em> on a store error for every call it
    /// guards. This one fails open only for a call on a socket that is already established — refusing
    /// those during a Redis incident would sign the whole instance out, which is the trade the
    /// interceptor makes and the reason it is made. On <c>OnConnectedAsync</c> it fails
    /// <em>closed</em>, and reads uncached, so a fifteen-second-old "no" cannot be the answer to a
    /// sign-out the user is watching for. <c>IdentityInteraction.IsRefreshRevokedAsync</c> fails
    /// closed outright, because minting a fresh credential is not something to do on a guess.</para>
    /// </remarks>
    /// <param name="onConnect">
    /// Whether this is the gate on a brand new connection, which decides both the caching and the
    /// direction of the failure. See the remarks.
    /// </param>
    private Task<bool> IsSessionRevokedAsync(bool onConnect = false)
    {
        // An identity the gate cannot parse is refused, not waved through: everything below is a
        // lookup keyed on these two, and "no id to look up" is not the same answer as "not revoked".
        if (!Guid.TryParse(Context.UserIdentifier, out var userId))
            return Task.FromResult(true);
        if (!Guid.TryParse(Context.User?.FindFirstValue("sid"), out var sid))
            return Task.FromResult(true);

        // The presence sid the caller chose, plus every credential id the server minted for it.
        return SessionRevocation.IsRevokedAsync(
            cacheDb, onConnect ? null : cache, userId,
            [sid, .. CredentialSessionIds()], TicketIssuedAt(), failClosed: onConnect, logger);
    }

    /// <summary>Every server-minted credential id this ticket carries.</summary>
    /// <remarks>
    /// Repeated claims rather than one joined value — a device may hold more than one credential —
    /// and unparseable ones are dropped rather than refused, because a claim that names no session
    /// cannot be matched against a tombstone either way.
    /// </remarks>
    private List<Guid> CredentialSessionIds()
    {
        var ids = new List<Guid>();

        foreach (var claim in Context.User?.FindAll(SessionRevocation.CredentialTicketClaim) ?? [])
        {
            if (Guid.TryParse(claim.Value, out var credentialSessionId))
                ids.Add(credentialSessionId);
        }

        return ids;
    }

    /// <summary>When this ticket was minted, as it says itself.</summary>
    /// <remarks>
    /// The smallest of the values, because a payload can end up carrying more than one <c>iat</c> —
    /// the claim <c>PickTicket</c> writes and whatever the token library adds of its own — and the
    /// oldest is the conservative reading against a floor. Null for a ticket minted before the claim
    /// existed, which <see cref="SessionRevocation.IsBelowFloor"/> treats as older than any floor.
    /// </remarks>
    private DateTimeOffset? TicketIssuedAt()
    {
        long? oldest = null;

        foreach (var claim in Context.User?.FindAll(JwtRegisteredClaimNames.Iat) ?? [])
        {
            if (long.TryParse(claim.Value, out var seconds) && (oldest is null || seconds < oldest))
                oldest = seconds;
        }

        return oldest is { } value ? DateTimeOffset.FromUnixTimeSeconds(value) : null;
    }

    /// <summary>Refuses the call, and the connection, when this session has been signed out.</summary>
    /// <remarks>
    /// It also renews the connection's liveness lease, because the two questions are asked in exactly
    /// the same places: every method that has to know whether this session may still act is a method
    /// that could only have been reached over a socket that is up. See <see cref="MarkSeen"/>.
    /// </remarks>
    /// <param name="markSeen">
    /// False for the two callers that reach the session grain themselves and so renew (or deliberately
    /// end) the lease on their own — a second one-way message per heartbeat, per connection, would buy
    /// nothing.
    /// </param>
    private async Task EnsureSessionIsLiveAsync(bool markSeen = true)
    {
        if (await IsSessionRevokedAsync())
        {
            // All three, because they answer different halves: the message tells the client to sign
            // out rather than reconnect, the abort stops a silent connection that makes no further
            // calls from receiving anything, the exception tells the caller why this one failed.
            await TellSignedOutAsync();
            Context.Abort();
            throw new HubException("this session has been signed out");
        }

        if (markSeen)
            MarkSeen();
    }

    /// <summary>
    /// Says so before the socket goes, so the client signs out at once instead of discovering it on
    /// its next ticket request — and instead of answering the close with a reconnect loop.
    /// </summary>
    /// <remarks>
    /// Same message, same reason code and the same bound as <see cref="HubConnectionRegistry"/> sends
    /// on a sign-out signal, so the client has one thing to listen for and one code to translate.
    /// Best effort: the refusal that follows does not depend on it arriving.
    /// </remarks>
    private async Task TellSignedOutAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

            await Clients.Caller.SendAsync(
                HubConnectionRegistry.SessionRevokedMessage, HubConnectionRegistry.SignedOutReason, timeout.Token);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Could not tell connection {ConnectionId} it was signed out", Context.ConnectionId);
        }
    }

    /// <summary>Tells the session this connection is still there.</summary>
    /// <remarks>
    /// <para>The session drops a connection nothing has been heard from for
    /// <c>PresenceTimingOptions.StaleConnectionAfter</c> and arms the grace when that was the last
    /// one, so being heard from is what keeps a user online. Until this, only <c>Heartbeat</c> said
    /// so, while <c>Resume</c>, both subscribes, both unsubscribes and all four typing spellings
    /// proved it just as conclusively and said nothing — every one of them is a client-to-server
    /// invocation, which SignalR cannot deliver over a socket that is not up.</para>
    ///
    /// <para>Fire-and-forget on purpose: the grain method is <c>[OneWay]</c>, so this is a message
    /// posted at the silo and not a round trip on a path a client hits while somebody types. A
    /// connection this session does not hold is a no-op at the other end, which is what makes it safe
    /// to call from the ungated unsubscribes as well.</para>
    /// </remarks>
    private void MarkSeen()
    {
        var sid = Context.User?.FindFirstValue("sid");
        if (Context.UserIdentifier is null || string.IsNullOrEmpty(sid))
            return;

        _ = factory.GetGrain<IUserSessionGrain>($"{Context.UserIdentifier}:{sid}")
           .MarkConnectionSeenAsync(Context.ConnectionId);
    }

    /// <summary>Sets the ambient ids for the grain calls this method is about to make.</summary>
    /// <remarks>
    /// Answers <c>false</c> when the ticket is missing a claim it needs, having already aborted the
    /// connection. It used to return <c>void</c>, so <c>OnConnectedAsync</c> carried on past an abort
    /// and built <see cref="SessionGrainKey"/> out of an empty sid — attaching a session grain keyed
    /// <c>"{userId}:"</c>, which no revocation could ever name.
    /// </remarks>
    private bool EnsureBeforeCall(bool isAllowAbort = false)
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
            return false;
        if (!takeClaim("mid", out var machineId))
            return false;

        RequestContext.AllowCallChainReentrancy();
        this.SetUserId(Guid.Parse(userId));
        this.SetUserMachineId(machineId);
        this.SetUserSessionId(Guid.Parse(sessionId));
        return true;
    }

    /// <inheritdoc cref="EnsureBeforeCall"/>
    /// <remarks>
    /// For the hub methods, where returning quietly would look to the caller like the call had
    /// worked. <c>OnConnectedAsync</c> is the one that returns instead, because there is nobody to
    /// throw at yet.
    /// </remarks>
    private void RequireTicket()
    {
        if (!EnsureBeforeCall(true))
            throw new HubException("this ticket is missing the claims the hub needs");
    }

    public async override Task OnDisconnectedAsync(Exception? exception)
    {
        // Unconditional and first: the registry only ever holds connections that attached, so this is
        // a no-op for a refused one, and it must run even on the path below that returns early. A
        // handle left behind after the socket is gone is a sweep spent on nothing every period, for
        // the life of the process.
        registry.Detach(Context.ConnectionId);

        // Only if this connection ever attached — see AttachedItem. A refused connect is aborted by
        // SignalR through this same callback, and detaching there arms a grace reminder on a session
        // that never started.
        if (!Context.Items.ContainsKey(AttachedItem))
            return;

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
        RequireTicket();
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
    {
        // Ungated, still heard from: a client asking to receive less is a client that is there, and
        // the liveness lease is not a permission. See MarkSeen.
        MarkSeen();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"spaces/{spaceId}");
    }

    /// <summary>
    /// Channel-scoped subscription: the client joins only the channel it currently has open, so
    /// channel content (messages/typing/reactions) is delivered to viewers instead of the whole space.
    /// </summary>
    /// <remarks>
    /// <para>The damaging half of S11 — a channel group carries message content — and the one the
    /// desktop client really calls, so it has to keep succeeding for real members.
    /// <c>IChannelGrain</c> exposes no space accessor, so the channel's owning space and the caller's
    /// right to see it are resolved together by
    /// <see cref="IUserGrain.ResolveChannelSpaceIfMemberAsync"/>.</para>
    ///
    /// <para>Membership alone was not enough, and the gap was the whole point of the gate. The read
    /// path narrows further: <c>SpaceReadGrain.VisibleChannelsAsync</c> filters the roster through
    /// <c>ArgonEntitlement.ViewChannel</c>, so a member without it never sees a restricted channel
    /// listed and cannot read its history — while <c>ChannelGrain.FireChannel</c> publishes every
    /// message, edit, reaction and typing event to <c>channels/{id}</c>. A rank-and-file member who
    /// learned the id of a moderators-only channel (a mention, an audit row, a screenshot — channel
    /// ids travel) could subscribe and read it live, with no trace on any read path. The resolver now
    /// answers with the same entitlement the roster filter uses, so the two cannot disagree.</para>
    ///
    /// <para>The check holds at subscribe time only. A member whose roles or overwrites change while
    /// subscribed keeps the group until they disconnect; evicting them belongs with whatever raises
    /// the permission change, which has no view of the connections.</para>
    /// </remarks>
    public async Task SubscribeToChannel(Guid channelId)
    {
        RequireTicket();
        await EnsureSessionIsLiveAsync();

        if (await factory.GetGrain<IUserGrain>(UserId).ResolveChannelSpaceIfMemberAsync(channelId) is null)
            throw new HubException("not allowed to view this channel");

        await Groups.AddToGroupAsync(Context.ConnectionId, $"channels/{channelId}");
    }

    /// <inheritdoc cref="UnSubscribeToSpace"/>
    public async Task UnSubscribeToChannel(Guid channelId)
    {
        MarkSeen();
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"channels/{channelId}");
    }

    public async Task Heartbeat(UserStatus status)
    {
        // HeartBeatAsync stamps the connection itself, so there is nothing for MarkSeen to add.
        await EnsureSessionIsLiveAsync(markSeen: false);

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
        // Not stamped: this connection is on its way out, and renewing the lease of something that is
        // about to be dropped is at best a no-op and at worst a race with the drop.
        await EnsureSessionIsLiveAsync(markSeen: false);
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
    ///
    /// <para>Gated on the revocation check like every other state-changing method, all four spellings
    /// of it. Nothing here is a read, and these were the only calls a signed-out client could still
    /// make without tripping anything: it could go on injecting typing indicators into any channel it
    /// cared to name, indefinitely, because nothing else it did would ever be checked. The check is
    /// the cached one, so it costs a local lookup on the path a client hits while somebody types.</para>
    /// </remarks>
    public async Task IAmTypingIn(Guid spaceId, Guid channelId)
    {
        RequireTicket();
        await EnsureSessionIsLiveAsync();
        await factory.GetGrain<IChannelGrain>(channelId).OnTypingEmit();
    }

    /// <inheritdoc cref="IAmTypingIn"/>
    public async Task IAmStopTypingIn(Guid spaceId, Guid channelId)
    {
        RequireTicket();
        await EnsureSessionIsLiveAsync();
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
        RequireTicket();
        await EnsureSessionIsLiveAsync();
        await factory.GetGrain<IChannelGrain>(channelId).OnTypingEmit();
    }

    /// <inheritdoc cref="IAmTyping"/>
    [Obsolete("Send the space id: IAmStopTypingIn(spaceId, channelId). It cannot be routed across regions.")]
    public async Task IAmStopTyping(Guid channelId)
    {
        RequireTicket();
        await EnsureSessionIsLiveAsync();
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
    /// The receiving half: the ticket scheme a client authenticates the socket with, the policy the
    /// mapped hub requires, and the two background halves of revocation enforcement. Only a role that
    /// clients connect to needs it.
    /// </summary>
    /// <remarks>
    /// <para>The registry and the two services that drive it belong here rather than in
    /// <see cref="AddRealtimeBus"/>, and the split is the same one the two methods already make: a
    /// silo publishes events and holds no connections, so a registry there would always be empty and
    /// a subscriber there would have nothing to abort. Only a node that <em>maps</em> the hub can
    /// close a socket. See <see cref="AppHub"/> for the three layers.</para>
    ///
    /// <para><c>TryAdd</c> throughout so a role that reaches this twice — through
    /// <c>AppHubFeature</c> and through whatever required it — does not end up running two
    /// subscribers on one subject, which would abort every connection twice and log it twice.</para>
    /// </remarks>
    public static void AddAppHubEndpoint(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<HubConnectionRegistry>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, SessionRevocationSubscriber>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, HubConnectionSweeper>());

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