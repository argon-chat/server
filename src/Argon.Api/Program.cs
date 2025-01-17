using Argon.Api.Migrations;
using Argon.Features.Auth;
using Argon.Features.Captcha;
using Argon.Features.EF;
using Argon.Features.Env;
using Argon.Features.Jwt;
using Argon.Features.MediaStorage;
using Argon.Features.Middlewares;
using Argon.Features.OrleansStreamingProviders;
using Argon.Features.Otp;
using Argon.Features.Pex;
using Argon.Features.Repositories;
using Argon.Features.Template;
using Argon.Features.Web;
using Argon.Services;
using Argon.Sfu;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.UseMessagePack();
builder.ConfigureDefaultKestrel();
builder.Services.AddServerTiming();
builder.AddSentry();
builder.Services.Configure<SmtpConfig>(builder.Configuration.GetSection("Smtp"));
builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRedisClient("cache");
builder.AddNatsStreaming();
builder.AddPooledDatabase<ApplicationDbContext>();
builder.AddArgonAuthorization();
builder.AddJwt();

if (!builder.Environment.IsManaged())
{
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
}

app.Map("/IEventBus/SubscribeToMeEvents.wt", x =>
{
    x.Use(async (context, func) =>
    {
    #pragma warning disable CA2252
        var wt = context.Features.Get<IHttpWebTransportFeature>();

        if (wt is null)
            return;

        var session = await wt.AcceptAsync();
    #pragma warning restore CA2252

        await func(context);
    });
});

app.MapDefaultEndpoints();
app.MapGet("/", () => new
{
    version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
});

await app.WarpUp<ApplicationDbContext>().RunAsync();