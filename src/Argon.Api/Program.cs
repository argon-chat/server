using ActualLab.Fusion;
using ActualLab.Rpc;
using ActualLab.Rpc.Server;
using Argon.Api;
using Argon.Api.Entities;
using Argon.Api.Extensions;
using Argon.Api.Features;
using Argon.Api.Features.EmailForms;
using Argon.Api.Features.Jwt;
using Argon.Api.Features.Otp;
using Argon.Api.Grains.Interfaces;
using Argon.Api.Migrations;
using Argon.Api.Services;
using Argon.Contracts;
using Argon.Sfu;
using AutoMapper;

var builder = WebApplication.CreateBuilder(args);

builder.AddJwt();
builder.Services.Configure<SmtpConfig>(builder.Configuration.GetSection("Smtp"));
builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRabbitMQClient("rmq");
builder.AddNpgsqlDbContext<ApplicationDbContext>("DefaultConnection");
builder.Services.AddSingleton<IPasswordHashingService, PasswordHashingService>();
builder.Services.AddControllers().AddNewtonsoftJson();
builder.Services.AddFusion(RpcServiceMode.Server, true)
   .Rpc.AddWebSocketServer(true).Rpc
   .AddServer<IUserInteraction, UserInteraction>()
   .AddServer<IServerInteraction, ServerInteraction>()
   .AddServer<IEventBus, EventBusService>();
builder.AddSwaggerWithAuthHeader();
builder.Services.AddAuthorization();
builder.AddSelectiveForwardingUnit();
builder.Services.AddTransient<UserManagerService>();
builder.Services.AddSingleton<IFusionContext, FusionContext>();
builder.AddOtpCodes();
builder.AddOrleans();
builder.AddEMailForms();
builder.Services.AddKubeResources();
builder.Services.AddDataProtection();
builder.Services.AddAutoMapper(typeof(User).Assembly); // TODO
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
app.MapGet("/", () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});

var mapper = app.Services.GetRequiredService<IMapper>();

mapper.ConfigurationProvider.AssertConfigurationIsValid();

await app.WarpUp<ApplicationDbContext>().RunAsync();