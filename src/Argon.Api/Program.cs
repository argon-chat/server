using Argon.Api.Features.Utils;
using Argon.Api.Migrations;
using Argon.Features.Env;
using Argon.Features.HostMode;
using Argon.Features.RegionalUnit;
using Microsoft.AspNetCore.WebSockets;

JsonConvert.DefaultSettings = () => new JsonSerializerSettings()
{
    Converters =
    {
        new UlongEnumConverter<ArgonEntitlement>()
    }
};

var builder = await RegionalUnitApp.CreateBuilder(args);
if (builder.Environment.IsSingleInstance())
    builder.AddSingleInstanceWorkload();
else if (builder.Environment.IsSingleRegion())
    builder.AddSingleRegionWorkloads();
else
    builder.AddMultiRegionWorkloads();
builder.Services.AddWebSockets(x =>
{
    x.KeepAliveInterval = TimeSpan.FromMinutes(1);
    x.KeepAliveTimeout  = TimeSpan.MaxValue;
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