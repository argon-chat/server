namespace Argon.Features.HostMode;

using Argon.Api.Features.Orleans.Consul;
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
using Controllers;

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
                options.ListenAnyIP(5002, listenOptions =>
                {
                    if (File.Exists("/etc/tls/tls.crt") && File.Exists("/etc/tls/tls.key"))
                    {
                        listenOptions.UseHttps(x =>
                        {
                            x.ServerCertificate = X509Certificate2.CreateFromPemFile(
                                "/etc/tls/tls.crt",
                                "/etc/tls/tls.key"
                            );
                        });
                        listenOptions.DisableAltSvcHeader = false;
                        listenOptions.Protocols           = HttpProtocols.Http1AndHttp2AndHttp3;
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
        }

        builder.AddArgonTransport(x =>
        {
            x.AddService<IServerInteraction, ServerInteraction>();
            x.AddService<IUserInteraction, UserInteraction>();
            x.AddService<IEventBus, EventBusService>();
        });

        return builder;
    }

    public static WebApplicationBuilder AddSingleRegionWorkloads(this WebApplicationBuilder builder)
    {
        builder.AddDefaultWorkloadServices();
        if (builder.IsEntryPointRole() || builder.IsHybridRole())
        {
            builder.Services.AddControllers().AddApplicationPart(typeof(FilesController).Assembly)
               .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()));
            builder.AddSwaggerWithAuthHeader();
            builder.Services.AddAuthorization();
        }

        if (builder.Environment.IsGateway())
            builder.AddConsul("SiloConsul");
        if (builder.Environment.IsEntryPoint())
            builder.AddConsul("ClusterConsul");
        builder.AddKubeResources();
        builder.AddTemplateEngine();

        return builder;
    }

    public static WebApplicationBuilder AddMultiRegionWorkloads(this WebApplicationBuilder builder)
        => throw new InvalidOperationException();

    public static WebApplicationBuilder AddDefaultWorkloadServices(this WebApplicationBuilder builder)
    {
        builder.AddVaultConfiguration();
        builder.AddArgonCacheDatabase();
        builder.Services.AddServerTiming();
        builder.WebHost.UseQuic();
        builder.AddLogging();
        builder.UseMessagePack();
        builder.AddSentry();
        builder.AddServiceDefaults();
        builder.AddPooledDatabase<ApplicationDbContext>();
        builder.AddArgonAuthorization();
        builder.AddJwt();
        builder.AddRewrites();
        builder.AddContentDeliveryNetwork();
        builder.AddArgonPermissions();
        builder.AddSelectiveForwardingUnit();
        builder.AddOtpCodes();
        builder.AddEfRepositories();
        builder.AddCaptchaFeature();

        if (builder.IsEntryPointRole())
            builder.AddOrleansClient();
        else
            builder.AddOrleans();

        return builder;
    }
}


public static class RunHostModeExtensions
{
    public static WebApplication UseSingleInstanceWorkloads(this WebApplication app)
    {
        app.UseServerTiming();
        app.UseCors();
        app.UseSwagger();
        app.UseSwaggerUI();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapDefaultEndpoints();
        app.MapArgonTransport();
        app.MapDefaultEndpoints();
        app.MapGet("/", () => new {
            version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
        });
        return app;
    }


    public static WebApplication UseSingleRegionWorkloads(this WebApplication app)
    {
        app.UseServerTiming();

        if (app.Environment.IsEntryPoint())
        {
            app.UseCors();
            app.UseSwagger();
            app.UseSwaggerUI();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            app.MapDefaultEndpoints();
            app.MapArgonTransport();
            app.MapDefaultEndpoints();
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