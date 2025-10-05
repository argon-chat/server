using Argon.Api.Features.Bus;
using Argon.Api.Features.Utils;
using Argon.Api.Migrations;
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

builder.Services.AddIonProtocol((x) => {
    x.AddInterceptor<ArgonTransactionInterceptor>();
    x.AddInterceptor<ArgonOrleansInterceptor>();
    x.AddService<IUserInteraction, UserInteractionImpl>();
    x.AddService<IIdentityInteraction, IdentityInteraction>();
    x.AddService<IEventBus, EventBusImpl>();
    x.AddService<IServerInteraction, ServerInteractionImpl>();
    x.AddService<IChannelInteraction, ChannelInteractionImpl>();
    x.AddService<IInventoryInteraction, InventoryInteractionImpl>();
    x.AddService<IArchetypeInteraction, ArchetypeInteraction>();
    x.IonWithSubProtocolTicketExchange<IonTicketExchangeImpl>();
});

var app = builder.Build();

if (builder.Environment.IsSingleInstance())
    app.UseSingleInstanceWorkloads();
else if (builder.Environment.IsSingleRegion())
    app.UseSingleRegionWorkloads();
else
    app.UseMultiRegionWorkloads();

app.Use(async (context, func) =>
{
    await func(context);
});

await app.WarmUpCassandra();
await app.WarmUpRotations();
await app.WarmUp<ApplicationDbContext>().RunAsync();