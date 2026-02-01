namespace Argon.Services.Ion;

using System.IdentityModel.Tokens.Jwt;
using Api.Features.Bus;
using Consul;
using k8s.KubeConfigModels;
using Microsoft.IdentityModel.Tokens;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Claims;

public class EventBusImpl(ILogger<IEventBus> logger, IConfiguration configuration) : IEventBus
{
    public IAsyncEnumerable<IArgonEvent> ForServer(Guid spaceId, [EnumeratorCancellation] CancellationToken ct = default)
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
        await DispatchTree(ev, this.GetClusterClient(), this.GetSessionId(), ct);

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
        };
        var token = new JwtSecurityToken(
            issuer: "ticket.argon.gl",
            audience: "ticket.argon.gl",
            claims: claims,
            notBefore: now.UtcDateTime.AddSeconds(-2),
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(configuration["TicketJwt:Key"]!)), SecurityAlgorithms.HmacSha256));

        var jwt = new JwtSecurityTokenHandler().WriteToken(token);


        return jwt;
    }

    private async static IAsyncEnumerable<IArgonEvent> MergeStreams(
        IAsyncEnumerable<IArgonEvent> serverEvents,
        IAsyncEnumerable<IArgonClientEvent>? clientEvents,
        IClusterClient client,
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
                ? ProcessNextClientEventAsync(clientEnum, client, sessionId, logger) 
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
                    clientTask = ProcessNextClientEventAsync(clientEnum!, client, sessionId, logger);
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
        Guid sessionId,
        ILogger logger)
    {
        try
        {
            if (!await enumerator.MoveNextAsync()) 
                return false;
            
            await DispatchTree(enumerator.Current, client, sessionId);
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

    private async static ValueTask DispatchTree(IArgonClientEvent ev, IClusterClient client, Guid sessionId, CancellationToken ct = default)
    {
        var sessionGrain = client.GetGrain<IUserSessionGrain>(sessionId.ToString());

        switch (ev)
        {
            case IAmTypingEvent typing:
                await sessionGrain.OnTypingEmit(typing.channelId);
                break;
            case IAmStopTypingEvent stopTyping:
                await sessionGrain.OnTypingStopEmit(stopTyping.channelId);
                break;
            case HeartBeatEvent heartbeat:
                if (!await sessionGrain.HeartBeatAsync(heartbeat.status))
                    throw new InvalidOperationException("Session expired, dropping connection");
                break;
            case SubscribeToMySpaces:
                break;
        }
    }
}