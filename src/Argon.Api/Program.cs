using Argon.Api.Features.Bus;
using Argon.Api.Features.Utils;
using Argon.Api.Migrations;
using Argon.Features.Env;
using Argon.Features.HostMode;
using Argon.Features.RegionalUnit;
using Argon.Services.Ion;
using Npgsql;

AppContext.SetSwitch("Npgsql.EnablePreparedStatements", false);
//var       cs   = "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=argon-chat-v2;Include Error Detail=true;ConnectionIdleLifetime=15;ConnectionPruningInterval=10"; // замени под себя
//using var conn = new NpgsqlConnection(cs);
//conn.Open();

//using var cmd = new NpgsqlCommand("SELECT pg_typeof(@p)", conn);
//var       p   = new NpgsqlParameter("p", new DateOnly(2004, 4, 8));
//cmd.Parameters.Add(p);

//var result = cmd.ExecuteScalar();
//Console.WriteLine(result);

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