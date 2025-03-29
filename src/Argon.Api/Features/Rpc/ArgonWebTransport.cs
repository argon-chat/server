namespace Argon.Services;

using System.Buffers;
using System.Net.WebSockets;
using Features.Rpc;
using Microsoft.AspNetCore.Connections;

public class ArgonWebTransport(ILogger<IArgonWebTransport> logger) : IArgonWebTransport
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
            if (!Guid.TryParse(srvId, out var serverId))
            {
                conn.Abort(new ConnectionAbortedException("srv incorrect format"));
                return;
            }

            logger.LogInformation("Web Transport handled server stream, {serverId}", serverId);
            var stream = await clusterClient.Streams().CreateClientStream(serverId, sequence, eventId);

            await Task.WhenAll(HandleLoopAsync(stream, conn), HandleLoopReadingAsync(conn));
        }
        else
        {
            logger.LogInformation("Web Transport handled user stream, {serverId}", user.id);
            var sessionGrain = clusterClient.GetGrain<IUserSessionGrain>(Guid.NewGuid());
            await sessionGrain.BeginRealtimeSession(user.id, user.machineId, UserStatus.Online);
            var stream = await clusterClient.Streams().CreateClientStream(user.id, sequence, eventId);
            await Task.WhenAll(HandleLoopAsync(stream, conn), HandleLoopReadingAsync(conn));
            await sessionGrain.EndRealtimeSession();
        }
    }

    private async Task HandleLoopReadingAsync(ArgonTransportFeaturePipe ctx)
    {
        using var mem = MemoryPool<byte>.Shared.Rent(4096);
        try
        {
            while (!ctx.ConnectionClosed.IsCancellationRequested)
            {
                await ctx.WebSocket.ReceiveAsync(mem.Memory, CancellationToken.None);
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
            logger.LogInformation("Argon Transport reading closed, {ConnectionId}", ctx.ConnectionId);
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

                    var bytes = new byte[16];
                    var ignored = ctx.WebSocket.ReceiveAsync(bytes, CancellationToken.None);
                    await ctx.FlushAsync();
                    if (result.IsCompletedSuccessfully)
                        continue;
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
            logger.LogInformation("Argon Transport writer closed, {ConnectionId}, {WebSocketErrorCode}", ctx.ConnectionId, e.WebSocketErrorCode);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Argon Transport writer closed, {ConnectionId}", ctx.ConnectionId);
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