using ActualLab.Fusion;
using ActualLab.Rpc;
using ActualLab.Rpc.Server;
using Argon.Api;
using Argon.Api.Entities;
using Argon.Api.Extensions;
using Argon.Api.Features.Jwt;
using Argon.Api.Features.Rpc;
using Argon.Api.Migrations;
using Argon.Api.Services;
using Argon.Sfu;

var builder = WebApplication.CreateBuilder(args: args);

builder.AddJwt();
builder.AddServiceDefaults();
builder.AddRedisOutputCache(connectionName: "cache");
builder.AddRabbitMQClient(connectionName: "rmq");
builder.AddNpgsqlDbContext<ApplicationDbContext>(connectionName: "DefaultConnection");
builder.Services.AddSingleton<IPasswordHashingService, PasswordHashingService>();
builder.Services.AddControllers();
builder.Services.AddFusion(defaultServiceMode: RpcServiceMode.Server, setDefaultServiceMode: true);
// .Rpc.AddServer<IUserAuthorization, UserAuthorization>()
// .AddServer<IUserInteraction, UserInteractionService>()
// .AddWebSocketServer(true);
builder.AddSwaggerWithAuthHeader();
builder.Services.AddAuthorization();
builder.AddSelectiveForwardingUnit();
builder.Services.AddTransient<UserManagerService>();
builder.Services.AddTransient<IFusionServiceContext, FusionServiceContext>();
builder.AddOrleans();
var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();
app.UseWebSockets();
app.MapRpcWebSocketServer();
app.MapGet(pattern: "/", handler: () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});
await app.WarpUp<ApplicationDbContext>().RunAsync();