using System.Security.Cryptography.X509Certificates;
using Argon;
using Argon.Controllers;
using Argon.Extensions;
using Argon.Features.Env;
using Argon.Features.Jwt;
using Argon.Features.Logging;
using Argon.Features.MediaStorage;
using Argon.Features.Middlewares;
using Argon.Features.OrleansStreamingProviders;
using Argon.Features.Web;
using Argon.Services;
using Argon.Streaming;
using MessagePack;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Newtonsoft.Json.Converters;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Serialization;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddLogging();
builder.UseMessagePack();
builder.AddSentry();
builder.Services.AddServerTiming();
builder.WebHost.UseQuic();
builder.AddRedisClient("cache");
builder.AddRedisOutputCache("cache");
builder.WebHost.ConfigureKestrel(options => {
    options.ListenAnyIP(5002, listenOptions =>
    {
        listenOptions.UseHttps(x =>
        {
            x.ServerCertificate = X509Certificate2.CreateFromPemFile(
                "/etc/tls/tls.crt",
                "/etc/tls/tls.key"
            );
        });
        listenOptions.DisableAltSvcHeader = false;
        listenOptions.UseConnectionLogging();
        listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
    });
});
builder.AddContentDeliveryNetwork();
builder.AddServiceDefaults();
builder.AddNatsStreaming();
builder.AddJwt();
builder.AddRewrites();
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
        if (!builder.Environment.IsProduction())
            x.UseLocalhostClustering();
    });
builder.Services.UseKubernetesHosting();
builder.AddArgonTransport(x =>
{
    x.AddService<IServerInteraction, ServerInteraction>();
    x.AddService<IUserInteraction, UserInteraction>();
    x.AddService<IEventBus, EventBusService>();
});
if (builder.Environment.IsKube())
    builder.Services.UseKubernetesHosting();
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseServerTiming();
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();
app.MapArgonTransport();

if (builder.Environment.IsKube())
    app.UseSerilogRequestLogging();

app.UseRewrites();
app.MapGet("/", () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});

await app.RunAsync();