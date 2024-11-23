using ActualLab.Fusion;
using ActualLab.Rpc;
using ActualLab.Rpc.Server;
using Argon.Api;
using Argon.Api.Entities;
using Argon.Api.Extensions;
using Argon.Api.Features;
using Argon.Api.Features.Captcha;
using Argon.Api.Features.EF;
using Argon.Api.Features.Env;
using Argon.Api.Features.Jwt;
using Argon.Api.Features.MediaStorage;
using Argon.Api.Features.Otp;
using Argon.Api.Features.Pex;
using Argon.Api.Features.Repositories;
using Argon.Api.Features.Template;
using Argon.Api.Grains.Interfaces;
using Argon.Api.Migrations;
using Argon.Api.Services;
using Argon.Contracts;
using Argon.Sfu;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddSentry(builder.Configuration.GetConnectionString("Sentry"));
builder.Services.Configure<SmtpConfig>(builder.Configuration.GetSection("Smtp"));
builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRedisClient("cache");
builder.Services.AddDbContext<ApplicationDbContext>(x => x
   .EnableDetailedErrors().EnableSensitiveDataLogging().UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
   .AddInterceptors(new TimeStampAndSoftDeleteInterceptor()));

builder.Services.AddSingleton<IPasswordHashingService, PasswordHashingService>();
builder.Services.AddHttpContextAccessor();
if (!builder.Environment.IsManaged())
{
    builder.AddJwt();
    builder.Services.AddControllers().AddNewtonsoftJson();
    builder.Services.AddFusion(RpcServiceMode.Server, true).Rpc.AddWebSocketServer(true).Rpc.AddServer<IUserInteraction, UserInteraction>()
       .AddServer<IServerInteraction, ServerInteraction>().AddServer<IEventBus, EventBusService>();
    builder.AddSwaggerWithAuthHeader();
    builder.Services.AddAuthorization();
    builder.AddContentDeliveryNetwork();
}

builder.AddArgonPermissions();
builder.AddSelectiveForwardingUnit();
builder.Services.AddTransient<UserManagerService>();
builder.Services.AddSingleton<IFusionContext, FusionContext>();
builder.AddOtpCodes();
builder.AddOrleans();
builder.AddTemplateEngine();
builder.AddEfRepositories();
builder.AddKubeResources();
builder.AddCaptchaFeature();
builder.Services.AddDataProtection();
var app = builder.Build();

if (!builder.Environment.IsManaged())
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseWebSockets();
    app.MapRpcWebSocketServer();
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseHttpsRedirection();
    app.MapControllers();
}

app.MapDefaultEndpoints();
app.MapGet("/", () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});

await app.WarpUp<ApplicationDbContext>().RunAsync();