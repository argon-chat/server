using ActualLab.Fusion;
using ActualLab.Rpc;
using ActualLab.Rpc.Server;
using Argon.Api.Entities;
using Argon.Api.Extensions;
using Argon.Api.Filters;
using Argon.Api.Migrations;
using Argon.Api.Services;
using Argon.Contracts;
using Argon.Sfu;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRabbitMQClient("rmq");
builder.AddNpgsqlDbContext<ApplicationDbContext>("DefaultConnection");
builder.Services.AddControllers(opts => { opts.Filters.Add<InjectUsernameFilter>(); });
builder.Services.AddFusion(RpcServiceMode.Server, true)
    .Rpc.AddServer<IUserAuthorization, UserAuthorization>()
    .AddServer<IUserInteraction, UserInteractionService>()
    .AddWebSocketServer(true);
builder.AddSwaggerWithAuthHeader();
builder.AddJwt();
builder.Services.AddAuthorization();
builder.AddSelectiveForwardingUnit();
builder.Services.AddTransient<UserManagerService>();
builder.AddOrleans();
var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();
app.MapRpcWebSocketServer();
var buildTime = File.GetLastWriteTimeUtc(typeof(Program).Assembly.Location);
app.MapGet("/", () => new { buildTime });
await app.WarpUp<ApplicationDbContext>().RunAsync();