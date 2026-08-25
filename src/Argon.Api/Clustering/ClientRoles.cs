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
        features.Add<ForwardedHeadersFeature>();
        features.Add<RoutingFeature>();
        features.Add<ControllersFeature>();
        features.Add<WebSocketsFeature>();
        features.Add<RewritesFeature>();
        features.Add<HostHooksFeature>();
        features.Add<ClientLifecycleFeature>();

        features.Add<IonProtocolFeature>();
        features.Add<AppHubFeature>();
        features.Add<DiscoveryFeature>();
        features.Add<RegionRegistryFeature>();
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
        features.Add<RegionRegistryFeature>();
        features.Add<SentryFeature>();
        features.Add<ServerTimingFeature>();
        features.Add<DatabaseFeature>();

        features.Add<KestrelFeature>();
        features.Add<ForwardedHeadersFeature>();
        features.Add<RoutingFeature>();
        features.Add<HostHooksFeature>();
        features.Add<ClientLifecycleFeature>();

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
        features.Add<RegionRegistryFeature>();
        features.Add<SentryFeature>();

        features.Add<KestrelFeature>();
        features.Add<ForwardedHeadersFeature>();
        features.Add<RoutingFeature>();
        features.Add<HostHooksFeature>();
        features.Add<ClientLifecycleFeature>();

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
        features.Add<RegionRegistryFeature>();
        features.Add<SentryFeature>();
        features.Add<DatabaseFeature>();

        features.Add<KestrelFeature>();
        features.Add<ForwardedHeadersFeature>();
        features.Add<RoutingFeature>();
        features.Add<HostHooksFeature>();
        features.Add<ClientLifecycleFeature>();

        features.Add<AdminConsoleFeature>();
    }
}

/// <summary>
/// The identity server — where anyone signing into anything Argon publishes actually signs in.
/// </summary>
/// <remarks>
/// A client role, and one that opens no database connection of its own: everything it reads about
/// applications goes through <c>IDevTeamsGrain</c> and <c>IAppsManagementGrain</c>, and everything
/// about people through <c>IIdentityDirectoryGrain</c>. That is not tidiness — this role is the one
/// exposed to the whole internet, and the further it sits from the data the less a mistake on it
/// costs.
/// <para>
/// Separate from <c>entrypoint</c> for the same reason it was a separate service before: the product
/// and the thing that says who you are fail differently, are attacked differently, and should not
/// share a process. It is also the OAuth provider <c>account</c> and <c>admin</c> authenticate
/// against, so a topology that runs those without this one has consoles nobody can sign into.
/// </para>
/// </remarks>
public sealed class AegisRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Aegis;

    public string Description => "identity server — OAuth provider, sign-in, operator step-up";
    public bool   IsClient    => true;

    public void OnFeatures(IArgonFeatureRegistry features)
    {
        features.Add<TelemetryFeature>();
        features.Add<RegionRegistryFeature>();
        features.Add<SentryFeature>();
        features.Add<SentryTunnelFeature>();
        features.Add<ServerTimingFeature>();

        features.Add<KestrelFeature>();
        features.Add<RoutingFeature>();
        features.Add<ControllersFeature>();
        features.Add<HostHooksFeature>();
        features.Add<ClientLifecycleFeature>();

        features.Add<AegisFeature>();
    }
}
