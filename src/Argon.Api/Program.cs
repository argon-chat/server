using Argon.Features.Captcha;
using Argon.Features.EF;
using Argon.Features.Env;
using Argon.Features.Jwt;
using Argon.Features.MediaStorage;
using Argon.Features.OrleansStreamingProviders;
using Argon.Features.Otp;
using Argon.Features.Pex;
using Argon.Features.Repositories;
using Argon.Features.Template;
using Argon.Migrations;
using Argon.Services;
using Argon.Sfu;
using Newtonsoft.Json.Converters;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => {
    options.Limits.KeepAliveTimeout                  = TimeSpan.FromSeconds(400);
    options.AddServerHeader                          = false;
    options.Limits.Http2.MaxStreamsPerConnection     = 100;
    options.Limits.Http2.InitialConnectionWindowSize = 65535;
    options.Limits.Http2.KeepAlivePingDelay          = TimeSpan.FromSeconds(30);
    options.Limits.Http2.KeepAlivePingTimeout        = TimeSpan.FromSeconds(10);
});

builder.AddSentry(builder.Configuration.GetConnectionString("Sentry"));
builder.Services.Configure<SmtpConfig>(builder.Configuration.GetSection("Smtp"));
builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRedisClient("cache");
builder.AddNatsStreaming();
builder.Services.AddDbContext<ApplicationDbContext>(x => x
   .EnableDetailedErrors()
   .EnableSensitiveDataLogging()
   .UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
   .AddInterceptors(new TimeStampAndSoftDeleteInterceptor()));

builder.Services.AddSingleton<IPasswordHashingService, PasswordHashingService>();
builder.Services.AddHttpContextAccessor();
if (!builder.Environment.IsManaged())
{
    builder.AddJwt();
    builder.Services.AddControllers()
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
builder.Services.AddTransient<UserManagerService>();
builder.AddOtpCodes();
builder.AddOrleans();
builder.AddTemplateEngine();
builder.AddEfRepositories();
builder.AddKubeResources();
builder.AddCaptchaFeature();
builder.Services.AddSignalR();
builder.Services.AddDataProtection();
var app = builder.Build();

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
}

app.MapDefaultEndpoints();
app.MapGet("/", () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});

await app.WarpUp<ApplicationDbContext>().RunAsync();