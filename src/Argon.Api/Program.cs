using System.Buffers;
using Argon.Api.Migrations;
using Argon.Features.Env;
using Argon.Features.HostMode;
using Argon.Features.NatsStreaming;
using Argon.Features.RegionalUnit;
using Argon.Servers;

var ser = new ArgonEventSerializer();

var mem = new ArrayBufferWriter<byte>();

ser.Serialize(mem, new ChannelCreated(new Channel()
{
    Name = "yamete",
    Id = Guid.NewGuid(),
    ChannelType = ChannelType.Announcement,
    CreatedAt = DateTime.Now
}));

var erw = mem.WrittenMemory.ToArray();

var rr = ser.Deserialize(new ReadOnlySequence<byte>(erw));


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

await app.WarpUp<ApplicationDbContext>().RunAsync();
