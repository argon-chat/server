namespace Argon.Api.Clustering;

using Argon.Features.Middlewares;
using AccountContracts;
using Argon.Api.Features.AccountConsole;
using Argon.Features.AccountConsole;
using Argon.Features.Admin;
using ConsoleContracts;

using global::Sentry.Infrastructure;

/// <summary>
/// The Ion transport itself: maps the RPC route, and marks the role as one whose extra ports have to
/// be bound.
/// </summary>
/// <remarks>
/// Every feature that registers an Ion service requires this one, because registering a service is
/// not the same as serving it — a service on a port of its own needs the Kestrel binding as well as
/// the route, and a role that registered one without both would start clean and answer nothing.
/// <para>
/// Only the route is mapped here. Binding the extra ports happens once, after every feature has
/// configured, because <c>UseIonPorts</c> reads the port registry at the moment it is called and this
/// feature is ordered <i>before</i> the ones that fill it — see <c>ArgonFeatureContext.Ion</c>.
/// </para>
/// </remarks>
public sealed class IonEndpointsFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("ion-endpoints")
            .Describing("Ion port bindings and the RPC route")
            .After<RoutingFeature>();

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
        ctx.Ion(x =>
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
    /// <remarks>
    /// <c>PresenceFeature</c> because <c>AdminConsoleImpl</c> takes <c>IUserSessionNotifier</c> in its
    /// constructor — it pushes the consequences of an operator action to the sessions they land on —
    /// and presence is the only thing that registers one. Without it the console could not be built
    /// at all, and the role advertised the port regardless: the first operator call resolved the
    /// service and got a container error. It is a client role hosting no grains, so the fixture that
    /// walks every hosted grain's constructor had nothing here to walk.
    /// </remarks>
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("IAdminConsole on a port of its own")
            .Requires<OperatorAuthFeature>()
            .Requires<IonEndpointsFeature>()
            .Requires<PresenceFeature>()
            .After<RoutingFeature>()
            .Options<AdminConsoleOptions>("AdminConsole");

    public void Configure(ArgonFeatureContext ctx)
    {
        var port = ctx.Options<AdminConsoleOptions>().Port;

        ctx.Ion(x =>
        {
            x.AddService<IAdminConsole, AdminConsoleImpl>(port, true);
            x.AddInterceptor<OperatorAuthInterceptor>(port);
        });

        ctx.Builder.AddDiagnosticServices();
    }
}

public sealed class AccountConsoleFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("account-console")
            .Describing("the developer account console on a port of its own")
            .Requires<AccountConsoleAuthFeature>()
            .Requires<IonEndpointsFeature>()
            .After<RoutingFeature>()
            .Options<AccountConsoleOptions>("AccountConsole");

    public void Configure(ArgonFeatureContext ctx)
    {
        var options = ctx.Options<AccountConsoleOptions>();

        // A port of its own, with the interceptor scoped to it. The console authenticates against the
        // OAuth provider rather than an Argon session, so its interceptor must never see a call meant
        // for the first-party surface if the two ever share a process.
        ctx.Ion(x =>
        {
            x.AddService<IAccountConsole, AccountConsoleService>(options.Port, true);
            x.AddService<ITeamConsole, TeamConsoleService>(options.Port, true);
            x.AddService<IAppManagement, AppManagementService>(options.Port, true);
            x.AddInterceptor<AccountConsoleAuthInterceptor>(options.Port);
        });

        ctx.Services.AddHttpContextAccessor();
        ctx.Services.AddMemoryCache();
        ctx.Services.AddScoped<ITeamAccessChecker, TeamAccessChecker>();
    }

    /// <summary>
    /// The console's own page, when the deployment has it in the image rather than on a CDN.
    /// </summary>
    /// <remarks>
    /// The same call the identity server makes for the sign-in widget: compressed siblings, cache
    /// headers, and a client-routing fallback. Ion answers on a port of its own, so nothing here
    /// competes with it — this is the ordinary HTTP surface the role already has.
    /// </remarks>
    public void Map(ArgonEndpointContext ctx)
        => ctx.App.UseSpaStaticFiles(ctx.Options<AccountConsoleOptions>().StaticRoot);
}
