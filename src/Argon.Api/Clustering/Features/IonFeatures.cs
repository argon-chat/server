namespace Argon.Api.Clustering;

using ConsoleContracts;

using global::Sentry.Infrastructure;

public sealed class IonProtocolFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("ion")
            .Describing("Ion RPC services for the first-party clients")
            .Requires<ArgonAuthorizationFeature>()
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

        ctx.Builder.UseIonPorts();
    }

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapRpcEndpoints();
}

public sealed class AdminConsoleFeature : IArgonFeature
{
    private const int AdminPort = 8920;

    public static void Describe(IFeatureDescriptor d)
        => d.Describing($"IAdminConsole on port {AdminPort}")
            .Requires<OperatorAuthFeature>()
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
