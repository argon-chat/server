namespace Argon.Features.HostMode;

using System.Security.Cryptography.X509Certificates;
using Api.Features.CoreLogic.Messages;
using Argon.Api.Features.CoreLogic.Otp;
using Argon.Api.Features.CoreLogic.Social;
using Argon.Core.Features.Integrations.Captcha;
using Auth;
using EF;
using Env;
using GeoIP;
using global::Sentry.Infrastructure;
using Jwt;
using k8s;
using Logging;
using Logic;
using MediaStorage;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebSockets;
using Middlewares;
using Pex;
using RegionalUnit;
using Repositories;
using Sagas;
using Serilog;
using Services;
using Services.L1L2;
using Sfu;
using SnowflakeId.DependencyInjection;
using Template;
using Vault;
using Web;
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
            builder.Services.AddAuthorization();
        }
        if (builder.IsHybridRole())
            builder.AddTemplateEngine();


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
            builder.Services.AddAuthorization();
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
        builder.MapBetaOptions();
        builder.AddVaultConfiguration();
        builder.AddVaultClient();
        builder.Services.AddServerTiming();
        builder.WebHost.UseQuic();
        builder.AddLogging();
        builder.Services.AddMessagePipe();
        builder.WebHost.UseSentry(o => {
            o.Dsn                 = builder.Configuration.GetConnectionString("Sentry");
            o.Debug               = true;
            o.AutoSessionTracking = true;
            o.TracesSampleRate    = 1.0;
            o.ProfilesSampleRate  = 1.0;
            o.DiagnosticLogger    = new TraceDiagnosticLogger(SentryLevel.Debug);
        });
       
        builder.AddUserPresenceFeature();
        builder.AddArgonCacheDatabase();
        builder.AddArgonAuthorization();
        builder.AddJwt();
        builder.AddRewrites();
        builder.AddKineticaFSApi();
        builder.AddSelectiveForwardingUnit();
        builder.AddCaptchaFeature();
        builder.AddSocialIntegrations();
        builder.Services.AddWebSockets(x =>
        {
            x.KeepAliveInterval = TimeSpan.FromMinutes(1);
            x.KeepAliveTimeout  = TimeSpan.MaxValue;
        });

        if (!builder.IsEntryPointRole())
        {
            builder.AddBeforeMigrations();
            builder.AddPooledDatabase<ApplicationDbContext>();
            builder.AddCassandraPooledContext();
            builder.AddEfRepositories();
            builder.AddArgonPermissions();
            builder.AddSagas();
            builder.AddMessagesLayout();
            builder.Services.AddSnowflakeUniqueId(options => {
                options.DataCenterId  = 1;
                options.UseConsoleLog = true;
            });
            builder.AddOtpCodes();
            builder.AddArchetypesCache();
        }

        if (builder.IsHybridRole())
        {
            if (!builder.IsSingleInstance())
                throw new InvalidOperationException("Hybrid role is only allowed in single instance mode");
            builder.AddWorkerOrleans();
            builder.AddShimsForHybridRole();
        }
        else if (builder.IsEntryPointRole())
        {
            if (builder.IsSingleRegion())
                builder.AddSingleOrleansClient();
            else if (builder.IsMultiRegion())
                builder.AddMultiOrleansClient();
            //else
            //    throw new InvalidOperationException("Cannot determine configuration for entry point role");
        }
        else
            builder.AddWorkerOrleans();

        return builder;
    }
}


public static class RunHostModeExtensions
{
    public static WebApplication UseSingleInstanceWorkloads(this WebApplication app, bool hasMapRoot = true, bool hasMapHooks = true)
    {
        app.UseServerTiming();

        if (app.Environment.IsHybrid() || app.Environment.IsEntryPoint())
        {
            app.UseCors();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapRpcEndpoints();
            app.UseWebSockets();
        }
        if (hasMapRoot)
            app.MapGet("/", () => new {
                version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
            });
        if (hasMapHooks)
            app.UsePreStopHook();

        return app;
    }


    public static WebApplication UseSingleRegionWorkloads(this WebApplication app, bool hasMapRoot = true, bool hasMapHooks = true)
    {
        app.UseServerTiming();

        if (app.Environment.IsHybrid() || app.Environment.IsEntryPoint())
        {
            app.UseCors();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapRpcEndpoints();
            app.MapControllers();
            app.UseWebSockets();
            if (Environment.GetEnvironmentVariable("NO_STRUCTURED_LOGS") is null)
                app.UseSerilogRequestLogging();
            app.UseRewrites();
        }
        if (hasMapRoot)
            app.MapGet("/", () => new {
                version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
            });
        if (hasMapHooks)
            app.UsePreStopHook();

        return app;
    }

    public static WebApplication UseMultiRegionWorkloads(this WebApplication app)
        => throw new InvalidOperationException();
}