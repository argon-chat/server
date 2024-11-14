using Argon.Api;

var builder = WebApplication.CreateBuilder(args);

// builder.AddServiceDefaults();
// builder.AddJwt();
// builder.Services.AddControllers()
//    .AddApplicationPart(typeof(AuthorizationController).Assembly)
//    .AddNewtonsoftJson();
// builder.Services.AddFusion(RpcServiceMode.Server, true)
//    .Rpc.AddWebSocketServer(true).Rpc
//    .AddServer<IUserInteraction, UserInteraction>()
//    .AddServer<IServerInteraction, ServerInteraction>()
//    .AddServer<IEventBus, EventBusService>();
// builder.AddSwaggerWithAuthHeader();
// builder.Services.AddSerializer(x => {
//     x.AddMemoryPackSerializer();
// }).AddOrleansClient(x =>
// {
//     x.Configure<ClusterOptions>(cluster =>
//     {
//         cluster.ClusterId = "argonchat";
//         cluster.ServiceId = "argonchat";
//     }).AddStreaming();
//     if (builder.Environment.IsProduction())
//         x.UseKubeGatewayListProvider();
//     else
//         x.UseLocalhostClustering();
// });
// builder.Services.AddAuthorization();
// builder.Services.AddSingleton<IFusionContext, FusionContext>();
// builder.Services.AddAutoMapper(typeof(User).Assembly);
var app = builder.Build();

// app.UseWebSockets();
// app.MapRpcWebSocketServer();
// app.UseSwagger();
// app.UseSwaggerUI();
// app.UseHttpsRedirection();
// app.UseAuthentication();
// app.UseAuthorization();
// app.MapControllers();
// app.MapDefaultEndpoints();

app.MapGet("/", () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});

await app.RunAsync();