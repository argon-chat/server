using Argon.Core.Features.EF;
using Argon.Core.Features.Transport;
using Argon.Features.Env;
using Argon.Features.HostMode;
using Argon.Features.RegionalUnit;
using Argon.Services.Ion;
using Microsoft.AspNetCore.Http.Connections;


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
    x.IonWithSubProtocolTicketExchange<IonTicketExchangeImpl>();
});
builder.AddSignalRAppHub();
builder.Services.AddHttpClient();
builder.Services.AddSentryTunneling("sentry.argon.gl");

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