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
        var wt = ctx.Features.Get<IHttpWebTransportFeature>();

        if (wt is null)
            throw new InvalidOperationException(nameof(IHttpWebTransportFeature));

        if (!wt.IsWebTransportRequest)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.UpgradeRequired;
            return;
        }

        if (!ctx.Request.Query.TryGetValue("aat", out var attToken))
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        var session = await wt.AcceptAsync();

        var conn = await session.AcceptStreamAsync(ctx.RequestAborted);

        if (conn is null)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        var exchanger = ctx.RequestServices.GetRequiredService<ITransportExchange>();
        var token     = await exchanger.ExchangeToken(attToken.ToString(), ctx);


        if (!token.IsSuccess)
        {
            ctx.Response.Headers.TryAdd("X-Wt-Status", token.Error.ToString());
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        using var scope = ArgonTransportContext.CreateWt(ctx, token.Value, ctx.RequestServices);

        var logger        = ctx.RequestServices.GetRequiredService<ILogger<IHttpWebTransportFeature>>();
        var clusterClient = ctx.RequestServices.GetRequiredService<IClusterClient>();

        var user = scope.User;

        var sessionGrain = clusterClient.GetGrain<IFusionSessionGrain>(user.machineId);
        await sessionGrain.BeginRealtimeSession(user.id, user.machineId, UserStatus.Online);
        var stream = await clusterClient.Streams().CreateClientStream(user.id);

        try
        {
            await foreach (var item in stream.WithCancellation(conn.ConnectionClosed))
            {
                var msg    = MessagePackSerializer.Serialize(item.GetType(), item);
                var result = conn.Transport.Output.WriteAsync(msg);
                if (result.IsCompletedSuccessfully)
                    continue;
                break;
            }
        }
        catch (OperationCanceledException) { } // its ok 
        catch (Exception e)
        {
            conn.Abort(new ConnectionAbortedException("failed write pkg", e));
        }
        await sessionGrain.EndRealtimeSession();
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