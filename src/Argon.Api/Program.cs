using Argon.Api.Features.Bus;
using Argon.Core.Features.EF;
using Argon.Features.Env;
using Argon.Features.HostMode;
using Argon.Features.RegionalUnit;
using Argon.Services.Ion;


var builder = await RegionalUnitApp.CreateBuilder(args);
if (builder.Environment.IsSingleInstance())
    builder.AddSingleInstanceWorkload();
else if (builder.Environment.IsSingleRegion())
    builder.AddSingleRegionWorkloads();
else
    builder.AddMultiRegionWorkloads();
builder.Services.AddSingleton<SubscriptionController>();

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

await app.WarmUpRotations();
await app.WarmUp<ApplicationDbContext>();
await app.RunAsync();

