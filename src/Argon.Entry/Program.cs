using Argon;
using Argon.Controllers;
using Argon.Extensions;
using Argon.Features.Jwt;
using Argon.Features.MediaStorage;
using Argon.Features.Middlewares;
using Argon.Features.Web;
using Argon.Features.OrleansStreamingProviders;
using Argon.Services;
using Argon.Streaming;
using MessagePack;
using Newtonsoft.Json.Converters;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.UseMessagePack();
builder.AddSentry();
builder.Services.AddServerTiming();
builder.ConfigureDefaultKestrel();
builder.AddContentDeliveryNetwork();
builder.AddServiceDefaults();
builder.AddNatsStreaming();
builder.AddJwt();
builder.Services.AddControllers().AddApplicationPart(typeof(FilesController).Assembly)
   .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()));
builder.AddDefaultCors();
builder.AddSwaggerWithAuthHeader();
builder.Services
   .AddSerializer(x => x.AddMessagePackSerializer(null, null, MessagePackSerializer.DefaultOptions))
   .AddOrleansClient(x =>
{
    x.Configure<ClusterOptions>(builder.Configuration.GetSection("Orleans"))
       .AddStreaming()
       .AddPersistentStreams("default", NatsAdapterFactory.Create, options => { })
       .AddPersistentStreams(IArgonEvent.ProviderId, NatsAdapterFactory.Create, options => { })
       .AddBroadcastChannel(IArgonEvent.Broadcast);
    if (builder.Environment.IsProduction())
        x.UseKubeGatewayListProvider();
    else
        x.UseLocalhostClustering();
});
builder.AddArgonTransport(x =>
{
    x.AddService<IServerInteraction, ServerInteraction>();
    x.AddService<IUserInteraction, UserInteraction>();
    x.AddService<IEventBus, EventBusService>();
});
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseServerTiming();
app.UseCors();
app.UseWebSockets();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();
app.MapArgonTransport();

app.MapGet("/", () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});

await app.RunAsync();