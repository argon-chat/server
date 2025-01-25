namespace Argon.Services;

using System.Net;
using Argon.Features.Rpc;
using Features.Jwt;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

public static class RpcServiceCollectionExtensions
{
    public static void MapArgonTransport(this WebApplication app)
    {
        app.MapGrpcService<ArgonTransport>();
        app.UseGrpcWeb(new GrpcWebOptions()
        {
            DefaultEnabled = true
        });
        app.MapGet("/$wt", Handler);
        app.Map("/transport.wt", HandleWt);
    }


    private async static Task HandleWt(HttpContext ctx)
    {
        var wt     = ctx.Features.Get<IHttpWebTransportFeature>();
        var logger = ctx.RequestServices.GetRequiredService<ILogger<IHttpWebTransportFeature>>();


        if (wt is null)
        {
            logger.LogCritical("Failed getting asp feature {featureName}", nameof(IHttpWebTransportFeature));
            throw new InvalidOperationException(nameof(IHttpWebTransportFeature));
        }

        if (!wt.IsWebTransportRequest)
        {
            logger.LogCritical("Request is not web transport type");
            ctx.Response.StatusCode = (int)HttpStatusCode.UpgradeRequired;
            return;
        }

        if (!ctx.Request.Query.TryGetValue("aat", out var attToken))
        {
            logger.LogCritical("aat query in web transport request not found");
            ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        var session = await wt.AcceptAsync();

        logger.LogInformation("Web Transport session accepted, {wtSessionId}", session.SessionId);

        var conn = await session.AcceptStreamAsync(ctx.RequestAborted);

        logger.LogInformation("Web Transport stream created, {wtSessionId}", session.SessionId);

        if (conn is null)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        var exchanger = ctx.RequestServices.GetRequiredService<ITransportExchange>();
        var token     = await exchanger.ExchangeToken(attToken.ToString(), ctx);


        if (!token.IsSuccess)
        {
            logger.LogWarning("Web Transport failed exchange token, {wtSessionId}, {aat}, {wtExchangeError}", session.SessionId, attToken,
                token.Error);
            ctx.Response.Headers.TryAdd("X-Wt-Status", token.Error.ToString());
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        logger.LogInformation("Web Transport token exchanged, {wtSessionId}, {aat}", session.SessionId, attToken);


        using var scope = ArgonTransportContext.CreateWt(ctx, token.Value, ctx.RequestServices);

        var clusterClient = ctx.RequestServices.GetRequiredService<IClusterClient>();

        var user = scope.User;

        if (ctx.Request.Query.TryGetValue("srv", out var srvId) )
        {
            if (!Guid.TryParse(srvId, out var serverId))
            {
                conn.Abort(new ConnectionAbortedException("srv incorrect format"));
                return;
            }

            logger.LogInformation("Web Transport handled server stream, {wtSessionId}, {serverId}", session.SessionId, serverId);
            var stream = await clusterClient.Streams().CreateClientStream(serverId);
            await HandleLoopAsync(stream, conn, logger);
        }
        else
        {
            logger.LogInformation("Web Transport handled user stream, {wtSessionId}, {serverId}", session.SessionId, user.id);
            var sessionGrain = clusterClient.GetGrain<IFusionSessionGrain>(user.machineId);
            await sessionGrain.BeginRealtimeSession(user.id, user.machineId, UserStatus.Online);
            var stream = await clusterClient.Streams().CreateClientStream(user.id);
            await HandleLoopAsync(stream, conn, logger);
            await sessionGrain.EndRealtimeSession();
        }
    }


    private async static ValueTask HandleLoopAsync(IArgonStream<IArgonEvent> stream, ConnectionContext ctx, ILogger<IHttpWebTransportFeature> logger)
    {
        try
        {
            await foreach (var item in stream.WithCancellation(ctx.ConnectionClosed))
            {
                var msg    = MessagePackSerializer.Serialize(item.GetType(), item);
                var result = ctx.Transport.Output.WriteAsync(msg);
                if (result.IsCompletedSuccessfully)
                    continue;
                break;
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Web Transport closed, {wtConnectionId}", ctx.ConnectionId);

        } // its ok 
        catch (Exception e)
        {
            logger.LogCritical(e, "Web Transport closed with exception, {wtConnectionId}, {e}", ctx.ConnectionId, e);
            ctx.Abort(new ConnectionAbortedException("failed write pkg", e));
        }
    }


    // todo
    async static IAsyncEnumerable<T> CombineAsyncEnumerators<T>(params IAsyncEnumerable<T>[] enums)
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


    private async static Task Handler(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderNames.Authorization, out var token))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        await using var scope = context.RequestServices.CreateAsyncScope();

        var opt       = scope.ServiceProvider.GetRequiredService<IOptions<TransportOptions>>();
        var auth      = scope.ServiceProvider.GetRequiredService<TokenAuthorization>();
        var exchanger = scope.ServiceProvider.GetRequiredService<ITransportExchange>();
        var result    = await auth.AuthorizeByToken(token.ToString());

        if (!result.IsSuccess)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = result.Error
            });
            return;
        }

        var aat = await exchanger.CreateExchangeKey(token.ToString(), result.Value.id, result.Value.machineId);

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.Headers.TryAdd("X-Wt-Upgrade", opt.Value.Upgrade);
        if (!string.IsNullOrEmpty(opt.Value.CertificateFingerprint))
            context.Response.Headers.TryAdd("X-Wt-Fingerprint", opt.Value.CertificateFingerprint);
        context.Response.Headers.TryAdd("X-Wt-AAT", aat.ToString());
        await context.Response.WriteAsync("OK");
    }


    public static void AddArgonTransport(this WebApplicationBuilder builder, Action<ITransportRegistration> onRegistration)
    {
        var col = builder.Services;
        col.Configure<ArgonTransportOptions>(_ => { });
        col.AddSingleton<ArgonDescriptorStorage>();
        col.Configure<TransportOptions>(builder.Configuration.GetSection("Transport"));
        col.AddSingleton<ITransportExchange, TransportExchange>();
        var reg = new ArgonDescriptorRegistration(col);
        onRegistration(reg);
        col.AddGrpc(x => { x.Interceptors.Add<AuthInterceptor>(); });
    }


    public static IServiceCollection AddRpcService<TInterface, TImplementation>(this IServiceCollection services)
        where TInterface : class, IArgonService
        where TImplementation : class, TInterface
    {
        services.AddSingleton<TInterface, TImplementation>();

        services.Configure<ArgonTransportOptions>(options => { options.Services.Add(typeof(TInterface), typeof(TImplementation)); });

        return services;
    }
}

public interface ITransportRegistration
{
    ITransportRegistration AddService<TInterface, TImpl>()
        where TInterface : class, IArgonService
        where TImpl : class, TInterface;
}

public readonly struct ArgonDescriptorRegistration(IServiceCollection col) : ITransportRegistration
{
    public ITransportRegistration AddService<TInterface, TImpl>() where TInterface : class, IArgonService where TImpl : class, TInterface
    {
        col.AddRpcService<TInterface, TImpl>();
        return this;
    }
}