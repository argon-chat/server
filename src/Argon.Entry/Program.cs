using ActualLab.Fusion;
using ActualLab.Rpc;
using ActualLab.Rpc.Server;
using ActualLab.Serialization;
using Argon.Api;
using Argon.Api.Controllers;
using Argon.Api.Entities;
using Argon.Api.Extensions;
using Argon.Api.Features.Jwt;
using Argon.Api.Features.Pex;
using Argon.Api.Services;
using Argon.Api.Services.Fusion;
using Argon.Contracts;
using Newtonsoft.Json.Converters;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.AddSentry(builder.Configuration.GetConnectionString("Sentry"));
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout                  = TimeSpan.FromSeconds(400);
    options.AddServerHeader                          = false;
    options.Limits.Http2.MaxStreamsPerConnection     = 100;
    options.Limits.Http2.InitialConnectionWindowSize = 65535;
    options.Limits.Http2.KeepAlivePingDelay          = TimeSpan.FromSeconds(30);
    options.Limits.Http2.KeepAlivePingTimeout        = TimeSpan.FromSeconds(10);
});

builder.AddArgonPermissions();
builder.AddServiceDefaults();
builder.AddJwt();
builder.Services.AddControllers()
   .AddApplicationPart(typeof(AuthorizationController).Assembly)
   .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()));
builder.Services.AddCors(x =>
{
    x.AddDefaultPolicy(z =>
    {
        z.SetIsOriginAllowed(origin => new Uri(origin).Host == "localhost");
        z.AllowAnyHeader();
        z.AllowAnyMethod();
    });
});
builder.Services.AddFusion(RpcServiceMode.Server, true)
   .Rpc.AddWebSocketServer(true).Rpc
   .AddServer<IUserInteraction, UserInteraction>()
   .AddServer<IServerInteraction, ServerInteraction>()
   .AddServer<IEventBus, EventBusService>()
   .AddServer<IUserPreferenceInteraction, UserPreferenceInteraction>();
builder.AddSwaggerWithAuthHeader();
builder.Services.AddSerializer(x => x.AddMessagePackSerializer(null, null, MessagePackByteSerializer.Default.Options))
   .AddOrleansClient(x =>
    {
        x.Configure<ClusterOptions>(builder.Configuration.GetSection("Orleans")).AddStreaming();
        if (builder.Environment.IsProduction())
            x.UseKubeGatewayListProvider();
        else
            x.UseLocalhostClustering();
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<IFusionContext, FusionContext>();
var app = builder.Build();

app.UseCors();
app.UseWebSockets();
app.MapRpcWebSocketServer();
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();

app.MapGet("/", () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});

await app.RunAsync();