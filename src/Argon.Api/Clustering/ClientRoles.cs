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

public sealed class AccountConsoleRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Account;

    public string Description => "developer account console — accounts, dev teams and their apps";
    public bool   IsClient    => true;

    // No DatabaseFeature: everything this role reads or writes goes through IDevTeamsGrain, so it
    // never opens a connection of its own. That is the whole reason the console's repository became
    // a grain rather than being carried over as a service.
    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<SentryFeature>();

        features.Add<KestrelFeature>();
        features.Add<RoutingFeature>();
        features.Add<HostHooksFeature>();

        features.Add<AccountConsoleFeature>();
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
