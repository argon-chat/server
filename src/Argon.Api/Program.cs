using Argon.Api.Migrations;
using Argon.Features.Env;
using Argon.Features.HostMode;
using Argon.Features.RegionalUnit;
using Argon.Features.Template;
using Argon.Grains;
using Fluid;

var builder = await RegionalUnitApp.CreateBuilder(args);
if (builder.Environment.IsSingleInstance())
    builder.AddSingleInstanceWorkload();
else if (builder.Environment.IsSingleRegion())
    builder.AddSingleRegionWorkloads();
else
    builder.AddMultiRegionWorkloads();

var app = builder.Build();

if (builder.Environment.IsSingleInstance())
    app.UseSingleInstanceWorkloads();
else if (builder.Environment.IsSingleRegion())
    app.UseSingleRegionWorkloads();
else
    app.UseMultiRegionWorkloads();


await app.WarmUpRotations();
await app.WarmUp<ApplicationDbContext>().RunAsync();