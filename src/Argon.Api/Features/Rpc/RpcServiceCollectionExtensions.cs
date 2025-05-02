namespace Argon.Services;

using System.Net;
using Features.Jwt;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Net.Http.Headers;
using Shared.SharedGrains;

public static class RpcServiceCollectionExtensions
{
    public static void MapArgonTransport(this WebApplication app)
    {
        app.MapGrpcService<ArgonTransport>();
        app.UseGrpcWeb(new GrpcWebOptions()
        {
            DefaultEnabled = true
        });
        app.Map("/$at.http", Handler);
        app.Map("/$at.wt", HandleWt);
        app.Map("/$at.ws", HandleWs);
        app.UseWebSockets();
    }

    private async static Task HandleWs(HttpContext ctx)
    {
        var logger      = ctx.RequestServices.GetRequiredService<ILogger<IHttpWebSocketFeature>>();
        var wtCollector = ctx.RequestServices.GetRequiredService<IArgonWebTransport>();
        var registry    = ctx.RequestServices.GetRequiredService<IArgonDcRegistry>();

        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            logger.LogCritical("Request is not web socket type");
            ctx.Response.StatusCode = (int)HttpStatusCode.UpgradeRequired;
            return;
        }

        if (!ctx.Request.Query.TryGetValue("aat", out var attToken))
        {
            logger.LogCritical("aat query in web transport request not found");
            ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return;
        }

        var socket    = await ctx.WebSockets.AcceptWebSocketAsync();
        var exchanger = ctx.RequestServices.GetRequiredService<ITransportExchange>();
        var token     = await exchanger.ExchangeToken(attToken.ToString(), ctx);

        if (!token.IsSuccess)
        {
            logger.LogWarning("WebSocket failed exchange token, {aat}, {wtExchangeError}", attToken,
                token.Error);
            ctx.Response.Headers.TryAdd("X-Wt-Status", token.Error.ToString());
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        logger.LogInformation("WebSocket token exchanged, {aat}", attToken);

        using var       scope = ArgonTransportContext.CreateWt(ctx, token.Value, ctx.RequestServices, registry);
        await using var pipe  = ArgonTransportFeaturePipe.CreateForWs(socket);

        await wtCollector.HandleTransportRequest(ctx, pipe, scope);
    }

    private async static Task HandleWt(HttpContext ctx)
    {
        var wt          = ctx.Features.Get<IHttpWebTransportFeature>();
        var logger      = ctx.RequestServices.GetRequiredService<ILogger<IHttpWebTransportFeature>>();
        var wtCollector = ctx.RequestServices.GetRequiredService<IArgonWebTransport>();
        var registry    = ctx.RequestServices.GetRequiredService<IArgonDcRegistry>();

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

        var session = await wt.AcceptAsync(ctx.RequestAborted);

        logger.LogInformation("Web Transport session accepted");
        var conn = await session.AcceptStreamAsync(ctx.RequestAborted);


        logger.LogInformation("Web Transport stream created");

        if (conn is null)
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        var exchanger = ctx.RequestServices.GetRequiredService<ITransportExchange>();
        var token     = await exchanger.ExchangeToken(attToken.ToString(), ctx);


        if (!token.IsSuccess)
        {
            logger.LogWarning("Web Transport failed exchange token, {aat}, {wtExchangeError}", attToken,
                token.Error);
            ctx.Response.Headers.TryAdd("X-Wt-Status", token.Error.ToString());
            ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }


        logger.LogInformation("Web Transport token exchanged, {aat}", attToken);


        using var       scope = ArgonTransportContext.CreateWt(ctx, token.Value, ctx.RequestServices, registry);
        await using var pipe  = ArgonTransportFeaturePipe.CreateForWt(conn);
        await wtCollector.HandleTransportRequest(ctx, pipe, scope);
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

        var   result    = await auth.AuthorizeByToken(token.ToString());
        Guid? sessionId;

        if (!result.IsSuccess)
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = result.Error
            });
            return;
        }

        if (context.Request.Headers.TryGetValue("X-Ctt", out var sid) && 
            context.Request.Headers.TryGetValue("X-Ctf", out var fingerprint))
        {
            var registry = scope.ServiceProvider.GetRequiredService<IArgonDcRegistry>();
            var dcClient = registry.GetNearestClusterClient();

            if (dcClient is null)
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "no dc available"
                });
                return;
            }

            sessionId = Guid.Parse(sid.ToString());
            var frags = fingerprint.ToString().Split(':');
            var appId = frags[0];
            var appFt = frags[1];

            var validated = await dcClient.GetGrain<IExternalClientCertificationGrain>(Guid.NewGuid())
               .ValidateCertificateFingerprint(appId, appFt, sessionId.Value, result.Value.id, result.Value.machineId);
            if (!validated)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "bad token"
                });
                return;
            }
        }
        else
            sessionId = context.GetSessionId();

        var aat = await exchanger.CreateExchangeKey(token.ToString(), result.Value.id, result.Value.machineId, sessionId.Value);

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
        col.AddSingleton<IArgonWebTransport, ArgonWebTransport>();
        var reg = new ArgonDescriptorRegistration(col);
        onRegistration(reg);
        col.AddGrpc(x => { x.Interceptors.Add<AuthInterceptor>(); });
        col.AddWebSockets(_ => { });
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