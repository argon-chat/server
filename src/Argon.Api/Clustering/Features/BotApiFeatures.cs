namespace Argon.Api.Clustering;

using global::Sentry.Infrastructure;

public sealed class BotPathTokenFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("rewrites /api/bot/{token}/… before routing can fail to match it")
            .Before<RoutingFeature>();

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.UseBotPathTokenAuth();
}

public sealed class BotApiFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("bot HTTP API, token auth and rate limiting")
            .Requires<BotPathTokenFeature>()
            .Requires<ArgonAuthorizationFeature>()
            .Requires<CacheFeature>()
            .Requires<AppHubFeature>()
            .After<RoutingFeature>()
            .Options<BotRateLimitOptions>(BotRateLimitOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Builder.AddBotRuntimeServices();

        ctx.Services.AddAuthentication()
           .AddScheme<AuthenticationSchemeOptions, BotTokenAuthenticationHandler>(
                BotTokenAuthenticationHandler.SchemeName, _ => { });

        ctx.Services.AddBotRateLimiting(ctx.Options<BotRateLimitOptions>());
        ctx.Services.AddBotApiJson();
        ctx.Services.AddHostedService<BotContractVerificationStartupFilter>();
    }

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapBotApi();
}
