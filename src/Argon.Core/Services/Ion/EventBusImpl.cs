namespace Argon.Services.Ion;

using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Argon.Features.Auth;
using Argon.Features.Logic;

public class EventBusImpl(
    ILogger<IEventBus> logger,
    IConfiguration configuration,
    IUserPresenceService presence,
    IArgonCacheDatabase cache,
    IOptions<ClientAppsOptions> clientApps) : IEventBus
{
    public IAsyncEnumerable<IArgonEvent> ForServer(Guid spaceId, CancellationToken ct = default)
    {
        throw new NotImplementedException();
        //var client = this.GetClusterClient();
        //await client.GetGrain<IUserSessionGrain>(this.GetSessionId().ToString()).BeginRealtimeSession();

        //var stream = await client.Streams().CreateClientStream(spaceId);
        //try
        //{
        //    await foreach (var e in stream.WithCancellation(ct))
        //        yield return e;
        //}
        //finally
        //{
        //    await stream.DisposeAsync();
        //}
    }
        
    public async Task Dispatch(IArgonClientEvent ev, CancellationToken ct = default) =>
        await DispatchTree(ev, this.GetClusterClient(), this.GetUserId(), this.GetSessionId(), ct);

    public IAsyncEnumerable<IArgonEvent> Pipe(IAsyncEnumerable<IArgonClientEvent>? ev, CancellationToken ct = default)
        => throw new NotImplementedException();

    //public async IAsyncEnumerable<IArgonEvent> Pipe(IAsyncEnumerable<IArgonClientEvent>? dispatchEvents, 
    //    [EnumeratorCancellation] CancellationToken ct = default)
    //{
    //    var sessionId = this.GetSessionId();
    //    var userId = this.GetUserId();
    //    var client = this.GetClusterClient();

    //    await client.GetGrain<IUserSessionGrain>(sessionId.ToString()).BeginRealtimeSession();

    //    var masterStream = await client.Streams().GetOrCreateSubscriptionCoupler(sessionId, userId, ct);
    //    var subscriptionTask = SubscribeToMySpacesAsync(userId, sessionId, client, ct);

    //    await foreach (var ev in MergeStreams(masterStream, dispatchEvents, client, sessionId, logger, ct))
    //        yield return ev;

    //    await subscriptionTask;
    //}

    public async Task<string> PickTicket(CancellationToken ct = default)
    {
        var userId    = this.GetUserId();
        var machineId = this.GetMachineId();
        var sid       = this.GetSessionId();
        var now       = DateTimeOffset.UtcNow;
        var expires   = now.Add(TimeSpan.FromDays(1));
        var claims = new List<Claim>
        {
            new("typ", "ticket"),
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new("sid", sid.ToString()),
            new("mid", machineId),
            // Written explicitly so the hub can place the ticket in time against the
            // sign-out-everywhere floor. A ticket is good for a day and an established socket is
            // never re-authenticated, so without this a password change leaves the compromised
            // client a full realtime feed for as long as its ticket lives.
            new(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };

        foreach (var credentialSessionId in await CredentialIdentitiesAsync(userId, sid, ct))
            claims.Add(new Claim(SessionRevocation.CredentialTicketClaim, credentialSessionId));

        var token = new JwtSecurityToken(
            issuer: "ticket.argon.gl",
            audience: "ticket.argon.gl",
            claims: claims,
            notBefore: now.UtcDateTime.AddSeconds(-2),
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(configuration["TicketJwt:Key"]!)), SecurityAlgorithms.HmacSha256));

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        // The ticket is the one call every realtime connection makes with the whole HTTP request
        // still in hand — address, country, User-Agent, the client's own description of itself — so
        // it is where a session is described for the devices screen and where the device history is
        // written. Once per session: a reconnect asks for a new ticket but finds the record in place.
        // The session grain used to write the history, but it is reached through the hub, whose
        // context carries the ids and nothing else, and every row it wrote said "unknown".
        try
        {
            var ctx       = this.GetRequestContext();
            var described = await presence.TouchSessionMetaAsync(userId, sid.ToString(),
                UserSessionMeta.Describe(ctx, clientApps.Value.Find(ctx.AppId, ctx.Client)), ct);

            if (described)
                await this.GetGrain<IUserGrain>(userId).UpdateUserDeviceHistory();
        }
        catch (Exception e)
        {
            // Naming a session is a courtesy to the devices screen, not a condition of connecting.
            logger.LogWarning(e, "Could not record the session description for {UserId}", userId);
        }

        return jwt;
    }

    /// <summary>
    /// The server-minted ids this connection is to be judged by, beside the <c>sid</c> the caller
    /// chose.
    /// </summary>
    /// <remarks>
    /// <para>Defect: revocation used to key on the presence sid alone, and the presence sid is
    /// whatever the client put in its <c>ArgonSecure</c> cookie. So a device that had been signed out
    /// reconnected under a fresh <c>scid</c> and every gate — the hub's, the grain's — looked up an
    /// id that had never been tombstoned and waved it through. Putting the credential id in the
    /// ticket makes the identity the hub tests one the caller cannot pick. See
    /// <see cref="SessionRevocation"/> for the whole model.</para>
    ///
    /// <para>Two sources, unioned, because neither covers the other. The request context carries the
    /// <c>sid</c> claim of the access token this very call presented — unforgeable, and present even
    /// for a caller that has just rotated its cookie. The credential mapping carries every id ever
    /// recorded against <em>this</em> presence sid, which is what an older client still gets, since
    /// its access token was minted before the claim existed. More ids can only mean more gates
    /// matching: a caller cannot escape by adding one, and cannot remove the one it did not write.</para>
    ///
    /// <para>Best effort on the store: a ticket is how a client connects at all, and a Redis blip
    /// must not become an outage. The cost of losing the mapping half is that revocation falls back
    /// to the token half, which is the stronger of the two anyway.</para>
    /// </remarks>
    private async Task<IReadOnlyCollection<string>> CredentialIdentitiesAsync(Guid userId, Guid sid, CancellationToken ct)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);

        if (this.GetRequestContext().Props.TryGetValue(SessionRevocation.CredentialSessionProperty, out var fromToken)
            && !string.IsNullOrWhiteSpace(fromToken))
            identities.Add(fromToken);

        try
        {
            foreach (var recorded in await SessionRevocation.CredentialSessionsAsync(cache, userId, sid, ct))
                identities.Add(recorded);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the credential sessions of {SessionId} for {UserId}", sid, userId);
        }

        return identities;
    }

    private async static IAsyncEnumerable<IArgonEvent> MergeStreams(
        IAsyncEnumerable<IArgonEvent> serverEvents,
        IAsyncEnumerable<IArgonClientEvent>? clientEvents,
        IClusterClient client,
        Guid userId,
        Guid sessionId,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var serverEnum = serverEvents.GetAsyncEnumerator(ct);
        var clientEnum = clientEvents?.GetAsyncEnumerator(ct);

        try
        {
            var serverTask = GetNextOrNullAsync(serverEnum);
            var clientTask = clientEnum != null
                ? ProcessNextClientEventAsync(clientEnum, client, userId, sessionId, logger)
                : Task.FromResult(false);

            while (!ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(serverTask, clientTask);

                if (completed == serverTask)
                {
                    var serverEvent = await serverTask;
                    if (serverEvent == null) break;

                    yield return serverEvent;
                    serverTask = GetNextOrNullAsync(serverEnum);
                }
                else
                {
                    if (!await clientTask) break;
                    clientTask = ProcessNextClientEventAsync(clientEnum!, client, userId, sessionId, logger);
                }
            }
        }
        finally
        {
            await DisposeEnumeratorSafelyAsync(serverEnum, logger);
            if (clientEnum != null)
            {
                await DisposeEnumeratorSafelyAsync(clientEnum, logger);
            }
        }
    }

    private async static ValueTask DisposeEnumeratorSafelyAsync<T>(IAsyncEnumerator<T> enumerator, ILogger logger)
    {
        try
        {
            await enumerator.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (WebSocketException)
        {
            // Expected when client disconnects abruptly
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error disposing enumerator");
        }
    }

    private async static Task<IArgonEvent?> GetNextOrNullAsync(IAsyncEnumerator<IArgonEvent> enumerator)
    {
        try
        {
            return await enumerator.MoveNextAsync() ? enumerator.Current : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (WebSocketException)
        {
            // Client disconnected
            return null;
        }
    }

    private async static Task<bool> ProcessNextClientEventAsync(
        IAsyncEnumerator<IArgonClientEvent> enumerator,
        IClusterClient client,
        Guid userId,
        Guid sessionId,
        ILogger logger)
    {
        try
        {
            if (!await enumerator.MoveNextAsync())
                return false;

            await DispatchTree(enumerator.Current, client, userId, sessionId);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WebSocketException)
        {
            // Client disconnected abruptly - this is expected
            logger.LogDebug("Client disconnected during event processing");
            return false;
        }
        catch (IOException)
        {
            // Connection was aborted - also expected
            logger.LogDebug("Connection aborted during event processing");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected error processing client event");
            return false;
        }
    }

    private async static ValueTask SubscribeToMySpacesAsync(Guid userId, Guid sessionId, IClusterClient client, CancellationToken ct = default)
    {
        //var spaceIds = await client.GetGrain<IUserGrain>(userId).GetMyServersIds(ct);
        //var tasks    = spaceIds.Select(spaceId => client.Streams().AssignSubscribe(sessionId, spaceId).AsTask());
        //await Task.WhenAll(tasks);
    }

    private async static ValueTask DispatchTree(IArgonClientEvent ev, IClusterClient client, Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        // Session grain is keyed "{userId}:{sid}". This legacy Ion path has no transport ConnectionId
        // and — crucially — no transport lifetime either, so it must not put anything into the live
        // connection set: it used to pass the sid itself as a pseudo-connection, which nothing ever
        // detached, so after every real socket dropped the grain still counted one attached
        // connection, armed no grace, and kept the user online until the silo restarted (S10).
        // TouchAsync is the connection-less form of the same keep-alive.
        var sessionGrain = client.GetGrain<IUserSessionGrain>($"{userId}:{sessionId}");

        switch (ev)
        {
            case IAmTypingEvent typing:
                await sessionGrain.OnTypingEmit(typing.channelId);
                break;
            case IAmStopTypingEvent stopTyping:
                await sessionGrain.OnTypingStopEmit(stopTyping.channelId);
                break;
            case HeartBeatEvent heartbeat:
                // false means either the sid has been signed out or the session has not started:
                // TouchAsync keeps a session alive but never mints one, so this RPC cannot create a
                // live row with no transport behind it (S10 removed the immortality; the grain-side
                // refusal removed the creation). Either way the caller is told to drop.
                if (!await sessionGrain.TouchAsync(heartbeat.status))
                    throw new InvalidOperationException("Session expired, dropping connection");
                break;
            case SubscribeToMySpaces:
                break;
        }
    }
}