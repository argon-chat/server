using ActualLab.Fusion;
using ActualLab.Rpc;
using ActualLab.Rpc.Server;
using Argon.Api;
using Argon.Api.Entities;
using Argon.Api.Extensions;
using Argon.Api.Features;
using Argon.Api.Features.Captcha;
using Argon.Api.Features.Env;
using Argon.Api.Features.Jwt;
using Argon.Api.Features.MediaStorage;
using Argon.Api.Features.Otp;
using Argon.Api.Features.Pex;
using Argon.Api.Features.Template;
using Argon.Api.Grains.Interfaces;
using Argon.Api.Migrations;
using Argon.Api.Services;
using Argon.Contracts;
using Argon.Sfu;
using AutoMapper;
using NATS.Client.JetStream.Models;
using NATS.Net;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SmtpConfig>(builder.Configuration.GetSection("Smtp"));
builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRedisClient("cache");

#region ToFix

// TODO: Yuuki said he knows a way to make this look elegant, until then, this is the best we have

var natsConnectionString = builder.Configuration.GetConnectionString("nats") ?? throw new ArgumentNullException("Nats");
var natsClient           = new NatsClient(natsConnectionString);
var natsConnection       = natsClient.Connection;
var js                   = natsClient.CreateJetStreamContext();
var stream               = await js.CreateStreamAsync(new StreamConfig("ARGON_STREAM", ["argon.streams.*"]));
var consumer             = await js.CreateOrUpdateConsumerAsync("ARGON_STREAM", new ConsumerConfig("streamConsoomer"));

builder.Services.AddSingleton(natsClient);
builder.Services.AddSingleton(natsConnection);
builder.Services.AddSingleton(js);
builder.Services.AddSingleton(stream);
builder.Services.AddSingleton(consumer);

#endregion

builder.AddNpgsqlDbContext<ApplicationDbContext>("DefaultConnection");
builder.Services.AddSingleton<IPasswordHashingService, PasswordHashingService>();
builder.Services.AddHttpContextAccessor();
if (!builder.Environment.IsManaged())
{
    builder.AddJwt();
    builder.Services.AddAuthorization();
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
builder.AddKubeResources();
builder.AddCaptchaFeature();
builder.Services.AddDataProtection();
builder.Services.AddAutoMapper(typeof(User).Assembly); // TODO
var app = builder.Build();

if (!builder.Environment.IsManaged())
{
    app.UseWebSockets();
    app.MapRpcWebSocketServer();
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
}

app.MapDefaultEndpoints();

app.MapGet("/", () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});

var mapper = app.Services.GetRequiredService<IMapper>();

mapper.ConfigurationProvider.AssertConfigurationIsValid();

await app.WarpUp<ApplicationDbContext>().RunAsync();