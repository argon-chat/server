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


                reentrancy.SetUserId(user.id);
                reentrancy.SetUserMachineId(ctx.GetMachineId());
                reentrancy.SetUserSessionId(sessionId);
                reentrancy.SetUserCountry(ctx.GetRegion());

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
        using var mem = MemoryPool<byte>.Shared.Rent(4096);
        
        try
        {
            while (!ctx.ConnectionClosed.IsCancellationRequested)
            {
                var readResult = await ctx.WebSocket.ReceiveAsync(mem.Memory, CancellationToken.None);

                if (!allowHandlePackages) continue;

                try
                {
                    using var reentrancy = RequestContext.AllowCallChainReentrancy();

                    var pkg = MessagePackSerializer.Deserialize(typeof(IArgonEvent), mem.Memory[..readResult.Count], null, CancellationToken.None);
                    await eventCollector.ExecuteEventAsync((pkg as IArgonEvent)!);
                }
                catch (CodecNotFoundException e)
                {
                    ctx.Abort(new ConnectionAbortedException("failed write pkg [codec error]", e));
                    return;
                }
                catch (ArgonDropConnectionException e)
                {
                    ctx.Abort(new ConnectionAbortedException("failed write pkg", e));
                    return;
                }
                catch (Exception e)
                {
                    logger.LogInformation(e, "Failed write event from user");
                }
            }
        }
        catch (WebSocketException e) when (ctx.WebSocket.CloseStatus == (WebSocketCloseStatus?)4999)
        {
            logger.LogInformation("Argon Transport normally closed, {ConnectionId}, {WebSocketErrorCode}", ctx.ConnectionId, e.WebSocketErrorCode);
        }
        catch (WebSocketException e)
        {
            logger.LogError("Argon Transport reading exception failed, {ConnectionId}, {WebSocketErrorCode}", ctx.ConnectionId, e.WebSocketErrorCode);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Argon Transport reading closed, {ConnectionId}", ctx.ConnectionId);
        } // its ok 
        catch (Exception e)
        {
            logger.LogCritical(e, "Argon Transport closed with exception, {ConnectionId}, {e}", ctx.ConnectionId, e);
            ctx.Abort(new ConnectionAbortedException("failed write pkg", e));
        }
    }

    private async Task HandleLoopAsync(IArgonStream<IArgonEvent> stream, ArgonTransportFeaturePipe ctx)
    {
        logger.LogWarning("Argon Transport stream is start");

        try
        {
            await foreach (var item in stream.WithCancellation(ctx.ConnectionClosed))
            {
                var evType = item.GetType();
                logger.LogInformation("Success write event '{eventType}'", evType.Name);
                var msg = MessagePackSerializer.Serialize(evType, item);

                try
                {
                    var result = ctx.WriteAsync(msg);
                    if (result.IsCompletedSuccessfully)
                        continue;
                    logger.LogCritical("Package write '{eventType}' failed, not completed", evType.Name);
                    break;
                }
                catch (Exception e)
                {
                    logger.LogCritical(e, "Failed write '{eventType}' event to web transport stream", evType.Name);

                    if (ctx.ConnectionClosed.IsCancellationRequested)
                        break;
                }
            }
        }
        catch (WebSocketException e)
        {
            logger.LogWarning(e, "Argon Transport writer closed, {ConnectionId}, {WebSocketErrorCode}", ctx.ConnectionId, e.WebSocketErrorCode);
        }
        catch (OperationCanceledException e)
        {
            logger.LogWarning(e, "Argon Transport writer closed, {ConnectionId}", ctx.ConnectionId);
        } // its ok 
        catch (Exception e)
        {
            logger.LogCritical(e, "Argon Transport closed with exception, {ConnectionId}, {e}", ctx.ConnectionId, e);
            ctx.Abort(new ConnectionAbortedException("failed write pkg", e));
        }

        logger.LogInformation("Argon transport stream is ended");
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