namespace Argon.Api.Clustering;

/// <summary>Incoming channel webhooks: the anonymous POST endpoint external services call.</summary>
public sealed class IncomingWebhooksFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("incoming-webhooks")
            .Describing("POST /api/webhooks/{id}/{token}, posting into a channel")
            .After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddIncomingWebhookRateLimit();

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapIncomingWebhooks();
}
