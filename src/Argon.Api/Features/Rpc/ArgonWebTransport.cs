namespace Argon.Services;

using System.Buffers;
using System.Net.WebSockets;
using Argon.Api.Features.Bus;
using Grains;
using Microsoft.AspNetCore.Connections;
using Orleans.Serialization;

public class ArgonWebTransport(ILogger<IArgonWebTransport> logger, IEventCollector eventCollector) : IArgonWebTransport
{
    public async Task HandleTransportRequest(HttpContext ctx, ArgonTransportFeaturePipe conn, ArgonTransportContext scope)
    {
        var user          = scope.User;
        var sequence      = -1L;
        var eventId       = -1;
        var dcRegistry    = ctx.RequestServices.GetRequiredService<IArgonDcRegistry>();
        var clusterClient = dcRegistry.GetNearestClusterClient();

        if (clusterClient is null)
        {
            if (ctx.Response.HasStarted)
                throw new InvalidOperationException();

            ctx.Response.StatusCode = 423;
            await ctx.Response.WriteAsJsonAsync(new
            {
                message = "region offline"
            });
            return;
        }

        if (ctx.Request.Query.TryGetValue("sequence", out var sequenceStr))
        {
            if (!long.TryParse(sequenceStr.ToString(), out sequence))
            {
                sequence = -1;
                logger.LogInformation("Failed to read sequence number, string value: {sequence}", sequenceStr);
            }
        }

        if (ctx.Request.Query.TryGetValue("eventId", out var eventIdStr))
        {
            if (!int.TryParse(eventIdStr.ToString(), out eventId))
            {
                eventId = -1;
                logger.LogInformation("Failed to read eventId number, string value: {eventId}", eventIdStr);
            }
        }

        if (ctx.Request.Query.TryGetValue("srv", out var srvId))
        {
            try
            {
                if (!Guid.TryParse(srvId, out var serverId))
                {
                    logger.LogCritical("srv incorrect format");
                    conn.Abort(new ConnectionAbortedException("srv incorrect format"));
                    return;
                }

                logger.LogInformation("Web Transport handled server stream, {serverId}", serverId);
                var stream = await clusterClient.Streams().CreateClientStream(serverId);

                await Task.WhenAll(HandleLoopAsync(stream, conn), HandleLoopReadingAsync(conn));
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "failed execute server transport");
                conn.Abort(new ConnectionAbortedException("Exception when activate server transport"));
            }
        }
        else
        {
            try
            {
                var sessionId = scope.GetSessionId();
                logger.LogInformation("Web Transport handled user stream, {serverId}, sessionId: {sessionId}", user.id, sessionId);

                if (sessionId == Guid.Empty)
                    throw new Exception($"No session id defined in argon transport");

                using var reentrancy = RequestContext.AllowCallChainReentrancy();


                reentrancy.SetUserIp(scope.GetIpAddress());
                reentrancy.SetUserId(user.id);
                reentrancy.SetUserSessionId(sessionId);
                reentrancy.SetUserCountry(ctx.GetRegion());
                reentrancy.SetUserMachineId(scope.User.machineId); // todo

                var sessionGrain = clusterClient.GetGrain<IUserSessionGrain>(sessionId);
                await sessionGrain.BeginRealtimeSession(UserStatus.Online);
                var stream = await clusterClient.Streams().CreateClientStream(user.id);
                await Task.WhenAll(HandleLoopAsync(stream, conn), HandleLoopReadingAsync(conn, true));
                await sessionGrain.EndRealtimeSession();
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "failed execute transport for user");
                conn.Abort(new ConnectionAbortedException("Exception when activate user transport"));
            }
        }
    }

    private async Task HandleLoopReadingAsync(ArgonTransportFeaturePipe ctx, bool allowHandlePackages = false)
    {
        using var buffer = MemoryPool<byte>.Shared.Rent(4096);

        try
        {
            while (!ctx.ConnectionClosed.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await ctx.WebSocket.ReceiveAsync(buffer.Memory, CancellationToken.None);
                }
                catch (WebSocketException ex)
                {
                    logger.LogWarning(ex, "WebSocket receive failed: {ConnectionId}", ctx.ConnectionId);
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogInformation("WebSocket closed: {ConnectionId}, Status: {Status}, Desc: {Description}",
                        ctx.ConnectionId, ctx.WebSocket.CloseStatus, ctx.WebSocket.CloseStatusDescription);
                    break;
                }

                if (result.Count == 0 || !allowHandlePackages)
                    continue;

                try
                {
                    using var reentrancy = RequestContext.AllowCallChainReentrancy();

                    var pkg = (IArgonEvent?)MessagePackSerializer.Deserialize(
                        typeof(IArgonEvent),
                        buffer.Memory[..result.Count],
                        null,
                        CancellationToken.None);

                    if (pkg is not null)
                        await eventCollector.ExecuteEventAsync(pkg);
                }
                catch (CodecNotFoundException ex)
                {
                    logger.LogWarning(ex, "Codec error while reading package, closing connection: {ConnectionId}", ctx.ConnectionId);
                    ctx.Abort(new ConnectionAbortedException("Codec error in received package", ex));
                    return;
                }
                catch (ArgonDropConnectionException ex)
                {
                    logger.LogWarning(ex, "Protocol violation, dropping connection: {ConnectionId}", ctx.ConnectionId);
                    ctx.Abort(new ConnectionAbortedException("Protocol violation in received package", ex));
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogInformation(ex, "Failed to handle incoming package: {ConnectionId}", ctx.ConnectionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("WebSocket reading loop cancelled: {ConnectionId}", ctx.ConnectionId);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unhandled exception in reading loop: {ConnectionId}", ctx.ConnectionId);
            ctx.Abort(new ConnectionAbortedException("Fatal error in WebSocket loop", ex));
        }
    }


    private async Task HandleLoopAsync(IArgonStream<IArgonEvent> stream, ArgonTransportFeaturePipe ctx)
    {
        logger.LogWarning("Argon transport write-loop started: {ConnectionId}", ctx.ConnectionId);

        try
        {
            await foreach (var item in stream.WithCancellation(ctx.ConnectionClosed))
            {
                var evType = item.GetType();
                byte[] msg;

                try
                {
                    msg = MessagePackSerializer.Serialize(evType, item);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to serialize event '{EventType}': {ConnectionId}", evType.Name, ctx.ConnectionId);
                    continue; // skip bad event
                }

                try
                {
                    await ctx.WriteAsync(msg);
                    logger.LogDebug("Sent event '{EventType}' to client: {ConnectionId}", evType.Name, ctx.ConnectionId);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("Write loop cancelled: {ConnectionId}", ctx.ConnectionId);
                    break;
                }
                catch (WebSocketException ex)
                {
                    logger.LogWarning(ex, "WebSocket write failed: {ConnectionId}", ctx.ConnectionId);
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex, "Unexpected write failure for '{EventType}': {ConnectionId}", evType.Name, ctx.ConnectionId);
                    if (!ctx.ConnectionClosed.IsCancellationRequested)
                        ctx.Abort(new ConnectionAbortedException("WebSocket write failure", ex));
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Write loop gracefully cancelled: {ConnectionId}", ctx.ConnectionId);
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "WebSocket writer closed with error: {ConnectionId}", ctx.ConnectionId);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Write loop crashed: {ConnectionId}", ctx.ConnectionId);
            ctx.Abort(new ConnectionAbortedException("Fatal write error", ex));
        }
        finally
        {
            logger.LogInformation("Argon transport write-loop ended: {ConnectionId}", ctx.ConnectionId);
        }
    }

    public async IAsyncEnumerable<T> CombineAsyncEnumerators<T>(params IAsyncEnumerable<T>[] enums)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<T>();
        var tasks = enums.Select(async enumerable =>
        {
            await foreach (var item in enumerable)
            {
                await channel.Writer.WriteAsync(item);
            }
        }).ToList();

        _ = Task.WhenAll(tasks).ContinueWith(_ => channel.Writer.Complete());

        while (await channel.Reader.WaitToReadAsync())
        {
            while (channel.Reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }
}