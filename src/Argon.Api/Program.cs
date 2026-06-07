using Argon.Api.Features.AdminApi;
using Argon.Api.Features.AdminApi.Diagnostics;
using Argon.Core.Features.EF;
using Argon.Core.Features.Transport;
using Argon.Features.Admin;
using Argon.Features.BotApi;
using Argon.Features.Env;
using Argon.Features.HostMode;
using Argon.Features.Integrations.Klipy;
using Argon.Features.Logic;
using Argon.Features.Moderation;
using Argon.Features.RegionalUnit;
using Argon.Services.Ion;
using ConsoleContracts;
using Microsoft.AspNetCore.Http.Connections;

if (BotApiCli.TryHandleCommand(args))
    return;

var builder = await RegionalUnitApp.CreateBuilder(args);
if (builder.Environment.IsSingleInstance())
    builder.AddSingleInstanceWorkload();
else if (builder.Environment.IsSingleRegion())
    builder.AddSingleRegionWorkloads();
else
    builder.AddMultiRegionWorkloads();

builder.Services.AddIonProtocol((x) =>
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

    x.AddService<IAdminConsole, AdminConsoleImpl>(8920, true);
    x.AddInterceptor<OperatorAuthInterceptor>(8920);
});
builder.AddDiagnosticServices();
builder.AddSignalRAppHub();
builder.Services.AddHttpClient();
builder.Services.AddSentryTunneling("sentry.argon.gl");

builder.Services.AddAuthentication()
   .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, BotTokenAuthenticationHandler>(
        BotTokenAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddBotRateLimiting(builder.Configuration);
builder.Services.AddBotApiJson();
builder.Services.AddHostedService<BotContractVerificationStartupFilter>();
builder.Services.Configure<AccountDeletionOptions>(
    builder.Configuration.GetSection(AccountDeletionOptions.SectionName));
builder.AddKlipyFeature();

builder.AddContentModeration();
builder.AddReportSystem();
builder.AddOperatorAuth();
builder.UseIonPorts();

var app = builder.Build();

app.UseSentryTunneling("/k");
if (builder.Environment.IsSingleInstance())
    app.UseSingleInstanceWorkloads();
else if (builder.Environment.IsSingleRegion())
    app.UseSingleRegionWorkloads();
else
    app.UseMultiRegionWorkloads();
app.UseSentryTracing();

if (app.Environment.IsEntryPoint() || app.Environment.IsHybrid())
    app.MapHub<AppHub>("/w",
            options => options.Transports = HttpTransportType.ServerSentEvents | HttpTransportType.WebSockets | HttpTransportType.LongPolling)
       .RequireAuthorization(new AuthorizeAttribute
        {
            AuthenticationSchemes = "Ticket",
            Policy                = "ticket"
        });

await app.WarmUpRotations();
await app.WarmUp<ApplicationDbContext>();

await app.RunAsync();