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
using FluentValidation;
using GeoIP;
using global::Orleans.Serialization;
using global::Sentry.Infrastructure;
using Logic;
using RegionalUnit;
using Services.Validators;
using Social;

public static class HostModeExtensions
{
    public static WebApplicationBuilder AddSingleInstanceWorkload(this WebApplicationBuilder builder)
    {
        builder.AddDefaultWorkloadServices();
        builder.Services.AddServerTiming();

        if (builder.IsEntryPointRole())
        {
            builder.WebHost.ConfigureKestrel(options => {
                options.ListenAnyIP(5002, listenOptions => {
                    if (builder.IsUseLocalHostCerts())
                    {
                        static X509Certificate2 LoadLocalhostCerts(WebApplicationBuilder builder)
                        {
                            if (!File.Exists("localhost.pfx"))
                                throw new Exception("Argon running in single mode, ensure certificates with 'mkcert -pkcs12 -p12-file localhost.pfx localhost' command");

                            var cert = X509CertificateLoader.LoadPkcs12FromFile("localhost.pfx", "changeit");

                            var hash    = SHA256.HashData(cert.RawData);
                            var certStr = Convert.ToBase64String(hash);

                            builder.Configuration["Transport:CertificateFingerprint"] = certStr;

                            return cert;
                        }

                        listenOptions.UseHttps(LoadLocalhostCerts(builder));
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                    }
                    else if (File.Exists("/etc/tls/tls.crt") && File.Exists("/etc/tls/tls.key"))
                    {
                        listenOptions.UseHttps(x => {
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
            builder.AddDefaultCors();
            builder.Services.AddControllers()
               .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()));
            builder.AddSwaggerWithAuthHeader();
            builder.Services.AddAuthorization();
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
            builder.AddDefaultCors();
            builder.WebHost.ConfigureKestrel(options => {
                options.ListenAnyIP(5002, listenOptions => {
                    if (builder.IsUseLocalHostCerts())
                    {
                        static X509Certificate2 LoadLocalhostCerts(WebApplicationBuilder builder)
                        {
                            if (!File.Exists("localhost.pfx"))
                                throw new Exception("Argon running in single mode, ensure certificates with 'mkcert -pkcs12 -p12-file localhost.pfx localhost' command");

                            var cert = X509CertificateLoader.LoadPkcs12FromFile("localhost.pfx", "changeit");

                            var hash    = SHA256.HashData(cert.RawData);
                            var certStr = Convert.ToBase64String(hash);

                            builder.Configuration["Transport:CertificateFingerprint"] = certStr;

                            return cert;
                        }

                        listenOptions.UseHttps(LoadLocalhostCerts(builder));
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                    }
                    else if (File.Exists("/etc/tls/tls.crt") && File.Exists("/etc/tls/tls.key"))
                    {
                        listenOptions.UseHttps(x => {
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
            builder.Services.AddControllers()
               .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()));
            builder.AddSwaggerWithAuthHeader();
            builder.Services.AddAuthorization();
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
        throw null;
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
        builder.Services.AddMessagePipe();
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
            builder.AddArgonPermissions();
        }

        builder.Services.AddValidatorsFromAssembly(typeof(NewUserCredentialsInputValidator).Assembly);
        builder.AddArgonAuthorization();
        builder.AddJwt();
        builder.AddRewrites();
        builder.AddContentDeliveryNetwork();
        builder.AddSelectiveForwardingUnit();
        builder.AddOtpCodes();
        builder.AddCaptchaFeature();
        builder.AddUserPresenceFeature();
        builder.AddSocialIntegrations();
        builder.AddEventCollectorFeature(EventConfigurator.Configure);
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

        app.UseEventCollectorFeature();
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
        app.UseEventCollectorFeature();
        app.MapGet("/", () => new {
            version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
        });
        return app;
    }

    public static WebApplication UseMultiRegionWorkloads(this WebApplication app)
        => throw new InvalidOperationException();
}