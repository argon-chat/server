namespace Argon.Api.Clustering;

using Argon.Features.Clustering;

public sealed class EntryPointRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.EntryPoint;

    public string Description => "Ion protocol, SignalR hub, auth, webhooks";
    public bool   IsClient    => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<SentryFeature>();
        features.Add<SentryTunnelFeature>();
        features.Add<ServerTimingFeature>();
        features.Add<MessagePipeFeature>();
        features.Add<DatabaseFeature>();

        features.Add<KestrelFeature>();
        features.Add<RoutingFeature>();
        features.Add<ControllersFeature>();
        features.Add<WebSocketsFeature>();
        features.Add<RewritesFeature>();
        features.Add<HostHooksFeature>();

        features.Add<IonProtocolFeature>();
        features.Add<AppHubFeature>();
        features.Add<DiscoveryFeature>();
        features.Add<TemplateEngineFeature>();

        features.Add<PresenceFeature>();
        features.Add<CaptchaFeature>();
        features.Add<XsollaFeature>();
        features.Add<SocialFeature>();
        features.Add<GeoIpFeature>();
        features.Add<SfuFeature>();
    }
}

public sealed class BotApiRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.BotApi;

    public string Description => "bot HTTP API and gateway events";
    public bool   IsClient    => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<SentryFeature>();
        features.Add<ServerTimingFeature>();
        features.Add<DatabaseFeature>();

        features.Add<KestrelFeature>();
        features.Add<RoutingFeature>();
        features.Add<HostHooksFeature>();

        features.Add<BotApiFeature>();
    }
}

public sealed class AdminRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Admin;

    public string Description => "operator console";
    public bool   IsClient    => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<SentryFeature>();
        features.Add<DatabaseFeature>();

        features.Add<KestrelFeature>();
        features.Add<RoutingFeature>();
        features.Add<HostHooksFeature>();

        features.Add<AdminConsoleFeature>();
    }
}
