using Argon.Api.Migrations;
using Argon.Features.Auth;
using Argon.Features.Captcha;
using Argon.Features.EF;
using Argon.Features.Env;
using Argon.Features.Jwt;
using Argon.Features.Logging;
using Argon.Features.MediaStorage;
using Argon.Features.Middlewares;
using Argon.Features.Otp;
using Argon.Features.Pex;
using Argon.Features.Repositories;
using Argon.Features.Template;
using Argon.Features.Vault;
using Argon.Features.Web;
using Argon.Services;
using Argon.Sfu;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.AddVaultConfiguration();
builder.AddLogging();
builder.UseMessagePack();
builder.AddSentry();
builder.Services.Configure<SmtpConfig>(builder.Configuration.GetSection("Smtp"));
builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRedisClient("cache");
//builder.AddNatsStreaming();
builder.AddPooledDatabase<ApplicationDbContext>();
builder.AddArgonAuthorization();
builder.AddJwt();
builder.AddRewrites();

if (!builder.Environment.IsManaged())
{
    builder.Services.AddServerTiming();
    builder.ConfigureDefaultKestrel();
    builder.AddDefaultCors();
    builder.Services.AddControllers()
       .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()));
    builder.AddSwaggerWithAuthHeader();
    builder.Services.AddAuthorization();
    builder.AddArgonTransport(x =>
    {
        x.AddService<IServerInteraction, ServerInteraction>();
        x.AddService<IUserInteraction, UserInteraction>();
        x.AddService<IEventBus, EventBusService>();
    });
}

builder.AddContentDeliveryNetwork();
builder.AddArgonPermissions();
builder.AddSelectiveForwardingUnit();
builder.AddOtpCodes();
builder.AddOrleans();
builder.AddTemplateEngine();
builder.AddEfRepositories();
builder.AddKubeResources();
builder.AddCaptchaFeature();
var app = builder.Build();

app.UseServerTiming();

if (!builder.Environment.IsManaged())
{
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseWebSockets();
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapControllers();
    app.MapArgonTransport();
    app.UseRewrites();
}
else
    app.UseSerilogRequestLogging();
app.MapDefaultEndpoints();
app.MapGet("/", () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});

await app.WarpUp<ApplicationDbContext>().RunAsync();