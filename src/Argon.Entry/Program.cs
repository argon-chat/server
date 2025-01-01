using Argon;
using Argon.Controllers;
using Argon.Extensions;
using Argon.Features.Jwt;
using Argon.Features.MediaStorage;
using Argon.Features.Middlewares;
using Argon.Services;
using Argon.Streaming;
using MessagePack;
using MessagePack.Resolvers;
using Newtonsoft.Json.Converters;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;
using Orleans.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.AddSentry(builder.Configuration.GetConnectionString("Sentry"));
builder.Services.AddServerTiming();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.KeepAliveTimeout                  = TimeSpan.FromSeconds(400);
    options.AddServerHeader                          = false;
    options.Limits.Http2.MaxStreamsPerConnection     = 100;
    options.Limits.Http2.InitialConnectionWindowSize = 65535;
    options.Limits.Http2.KeepAlivePingDelay          = TimeSpan.FromSeconds(30);
    options.Limits.Http2.KeepAlivePingTimeout        = TimeSpan.FromSeconds(10);
});
builder.AddContentDeliveryNetwork();
builder.AddServiceDefaults();
builder.AddJwt();
builder.Services.AddControllers().AddApplicationPart(typeof(FilesController).Assembly)
   .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()));
builder.Services.AddCors(x =>
{
    x.AddDefaultPolicy(z =>
    {
        z.SetIsOriginAllowed(origin => true /*new Uri(origin).Host == "localhost"*/);
        z.AllowAnyHeader();
        z.AllowAnyMethod();
    });
});
builder.AddSwaggerWithAuthHeader();
var options = MessagePackSerializerOptions.Standard
   .WithResolver(CompositeResolver.Create(
        DynamicEnumAsStringResolver.Instance,
        EitherFormatterResolver.Instance,
        StandardResolver.Instance,
        ArgonEventResolver.Instance));
MessagePackSerializer.DefaultOptions = options;
builder.Services.AddSerializer(x => x.AddMessagePackSerializer(null, null, MessagePackSerializer.DefaultOptions)).AddOrleansClient(x =>
{
    x.Configure<ClusterOptions>(builder.Configuration.GetSection("Orleans"))
       .AddStreaming()
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