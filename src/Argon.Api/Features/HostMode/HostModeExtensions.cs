namespace Argon.Features.HostMode;

using Auth;
using EF;
using Jwt;
using Logging;
using Middlewares;
using Vault;
using Services;
using Web;
using Captcha;
using Env;
using MediaStorage;
using Otp;
using Pex;
using Repositories;
using Template;
using Sfu;
using Serilog;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Cryptography.X509Certificates;
using GeoIP;
using global::Orleans.Serialization;
using global::Sentry.Infrastructure;
using RegionalUnit;

public static class HostModeExtensions
{
    public static WebApplicationBuilder AddSingleInstanceWorkload(this WebApplicationBuilder builder)
    {
        builder.AddDefaultWorkloadServices();
        builder.Services.AddServerTiming();

        if (builder.Environment.IsSingleInstance() && builder.Environment.IsDevelopment())
            builder.ConfigureDefaultKestrel();

        if (builder.IsEntryPointRole())
        {
            builder.WebHost.ConfigureKestrel(options => {
                options.ListenAnyIP(5002, listenOptions => {
                    if (File.Exists("/etc/tls/tls.crt") && File.Exists("/etc/tls/tls.key"))
                    {
                        listenOptions.UseHttps(x => {
                            x.ServerCertificate = X509Certificate2.CreateFromPemFile(
                                "/etc/tls/tls.crt",
                                "/etc/tls/tls.key"
                            );
                        });
                        listenOptions.DisableAltSvcHeader = false;
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                    }
                    else
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;

                    listenOptions.UseConnectionLogging();
                });
            });
        }

        if (builder.IsEntryPointRole() || builder.IsHybridRole())
        {
            builder.Services.AddControllers()
               .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()));
            builder.AddSwaggerWithAuthHeader();
            builder.Services.AddAuthorization();
            builder.AddDefaultCors();
            builder.AddArgonTransport(x => {
                x.AddService<IServerInteraction, ServerInteraction>();
                x.AddService<IUserInteraction, UserInteraction>();
                x.AddService<IEventBus, EventBusService>();
            });
        }

        return builder;
    }

    public static WebApplicationBuilder AddSingleRegionWorkloads(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IArgonRegionalBus, ArgonRegionalBus>();
        builder.AddDefaultWorkloadServices();
        builder.AddGeoIpSupport();
        if (builder.IsEntryPointRole() || builder.IsHybridRole())
        {
            builder.Services.AddControllers()
               .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()));
            builder.AddSwaggerWithAuthHeader();
            builder.Services.AddAuthorization();
            builder.AddDefaultCors();
            builder.AddArgonTransport(x => {
                x.AddService<IServerInteraction, ServerInteraction>();
                x.AddService<IUserInteraction, UserInteraction>();
                x.AddService<IEventBus, EventBusService>();
            });
        }

        builder.AddKubeResources();
        builder.AddTemplateEngine();

        return builder;
    }

    public static WebApplicationBuilder AddMultiRegionWorkloads(this WebApplicationBuilder builder)
    {
        builder.AddSingleRegionWorkloads();
        // TODO
        return builder;
    }

    public static WebApplicationBuilder AddDefaultWorkloadServices(this WebApplicationBuilder builder)
    {
        builder.AddVaultConfiguration();
        builder.AddArgonCacheDatabase();
        builder.Services.AddServerTiming();
        builder.WebHost.UseQuic();
        builder.AddLogging();
        builder.UseMessagePack();
        builder.WebHost.UseSentry(o => {
            o.Dsn                 = builder.Configuration.GetConnectionString("Sentry");
            o.Debug               = true;
            o.AutoSessionTracking = true;
            o.TracesSampleRate    = 1.0;
            o.ProfilesSampleRate  = 1.0;
            o.DiagnosticLogger    = new TraceDiagnosticLogger(SentryLevel.Debug);
        });
        if (!builder.IsEntryPointRole())
        {
            builder.AddPooledDatabase<ApplicationDbContext>();
            builder.AddEfRepositories();
        }
        builder.AddArgonAuthorization();
        builder.AddJwt();
        builder.AddRewrites();
        builder.AddContentDeliveryNetwork();
        builder.AddArgonPermissions();
        builder.AddSelectiveForwardingUnit();
        builder.AddOtpCodes();
        builder.AddCaptchaFeature();
        builder.Services.AddSerializer(x => x.AddMessagePackSerializer(null, null, MessagePackSerializer.DefaultOptions));

        if (builder.IsHybridRole())
        {
            if (!builder.IsSingleInstance())
                throw new InvalidOperationException("Hybrid role is only allowed in single instance mode");
            builder.AddWorkerOrleans();
        }
        else if (builder.IsEntryPointRole())
        {
            if (builder.IsSingleRegion())
                builder.AddSingleOrleansClient();
            else if (builder.IsMultiRegion())
                builder.AddMultiOrleansClient();
            else
                throw new InvalidOperationException("Cannot determine configuration for entry point role");
        }
        else
            builder.AddWorkerOrleans();

        return builder;
    }
}


public static class RunHostModeExtensions
{
    public static WebApplication UseSingleInstanceWorkloads(this WebApplication app)
    {
        app.UseServerTiming();

        if (app.Environment.IsHybrid() || app.Environment.IsEntryPoint())
        {
            app.UseCors();
            app.UseSwagger();
            app.UseSwaggerUI();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapArgonTransport();
        }

        app.MapGet("/", () => new {
            version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
        });
        return app;
    }


    public static WebApplication UseSingleRegionWorkloads(this WebApplication app)
    {
        app.UseServerTiming();

        if (app.Environment.IsHybrid() || app.Environment.IsEntryPoint())
        {
            app.UseCors();
            app.UseSwagger();
            app.UseSwaggerUI();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapArgonTransport();
            if (Environment.GetEnvironmentVariable("NO_STRUCTURED_LOGS") is null)
                app.UseSerilogRequestLogging();
            app.UseRewrites();
        }

        app.MapGet("/", () => new {
            version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
        });
        return app;
    }

    public static WebApplication UseMultiRegionWorkloads(this WebApplication app)
        => throw new InvalidOperationException();
}