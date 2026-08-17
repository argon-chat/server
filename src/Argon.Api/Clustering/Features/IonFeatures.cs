namespace Argon.Api.Clustering;

using AccountContracts;
using Argon.Api.Features.AccountConsole;
using ConsoleContracts;

using global::Sentry.Infrastructure;

/// <summary>
/// The Ion transport itself: binds the extra ports services asked for and maps the RPC route.
/// </summary>
/// <remarks>
/// Every feature that registers an Ion service requires this one, because registering a service is
/// not the same as serving it — a service on a port of its own needs the Kestrel binding as well as
/// the route, and a role that registered one without both would start clean and answer nothing.
/// <para>
/// Port bindings are collected into Kestrel's own options callback, which runs after every feature
/// has configured, so this feature being ordered before the services that register them is fine.
/// </para>
/// </remarks>
public sealed class IonEndpointsFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("ion-endpoints")
            .Describing("Ion port bindings and the RPC route")
            .After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.UseIonPorts();

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapRpcEndpoints();
}

public sealed class IonProtocolFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("ion")
            .Describing("Ion RPC services for the first-party clients")
            .Requires<ArgonAuthorizationFeature>()
            .Requires<IonEndpointsFeature>()
            .After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Services.AddIonProtocol(x =>
        {
            x.AddInterceptor<ArgonTransactionInterceptor>();
            x.AddInterceptor<ArgonOrleansInterceptor>();
            x.AddService<IUserInteraction, UserInteractionImpl>();
            x.AddService<IIdentityInteraction, IdentityInteraction>();
            x.AddService<IEventBus, EventBusImpl>();
            x.AddService<IServerInteraction, ServerInteractionImpl>();
            x.AddService<IChannelInteraction, ChannelInteractionImpl>();
            x.AddService<IInventoryInteraction, InventoryInteractionImpl>();
            x.AddService<IArchetypeInteraction, ArchetypeInteraction>();
            x.AddService<ICallInteraction, CallInteraction>();
            x.AddService<IFriendsInteraction, FriendsInteractionImpl>();
            x.AddService<IUserChatInteractions, UserChatInteractionImpl>();
            x.AddService<ISecurityInteraction, SecurityInteractionImpl>();
            x.AddService<IFeatureFlagInteractions, FeatureFlagInteractions>();
            x.AddService<IPrivacyInteraction, PrivacyInteractionImpl>();
            x.AddService<IBotManagementInteraction, BotManagementInteractionImpl>();
            x.AddService<IUltimaInteraction, UltimaInteractionImpl>();
            x.AddService<IReportInteraction, ReportInteractionImpl>();
            x.AddService<IGifInteraction, GifInteractionImpl>();
            x.IonWithSubProtocolTicketExchange<IonTicketExchangeImpl>();
        });
    }
}

public sealed class AdminConsoleFeature : IArgonFeature
{
    private const int AdminPort = 8920;

    public static void Describe(IFeatureDescriptor d)
        => d.Describing($"IAdminConsole on port {AdminPort}")
            .Requires<OperatorAuthFeature>()
            .Requires<IonEndpointsFeature>()
            .After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Services.AddIonProtocol(x =>
        {
            x.AddService<IAdminConsole, AdminConsoleImpl>(AdminPort, true);
            x.AddInterceptor<OperatorAuthInterceptor>(AdminPort);
        });

        ctx.Builder.AddDiagnosticServices();
    }
}

public sealed class AccountConsoleFeature : IArgonFeature
{
    private const int AccountConsolePort = 8930;

    public static void Describe(IFeatureDescriptor d)
        => d.Named("account-console")
            .Describing($"the developer account console on port {AccountConsolePort}")
            .Requires<AccountConsoleAuthFeature>()
            .Requires<IonEndpointsFeature>()
            .After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
    {
        // A port of its own, with the interceptor scoped to it. The console authenticates against the
        // OAuth provider rather than an Argon session, so its interceptor must never see a call meant
        // for the first-party surface if the two ever share a process.
        ctx.Services.AddIonProtocol(x =>
        {
            x.AddService<IAccountConsole, AccountConsoleService>(AccountConsolePort, true);
            x.AddService<ITeamConsole, TeamConsoleService>(AccountConsolePort, true);
            x.AddService<IAppManagement, AppManagementService>(AccountConsolePort, true);
            x.AddInterceptor<AccountConsoleAuthInterceptor>(AccountConsolePort);
        });

        ctx.Services.AddHttpContextAccessor();
        ctx.Services.AddMemoryCache();
        ctx.Services.AddScoped<ITeamAccessChecker, TeamAccessChecker>();
    }
}
