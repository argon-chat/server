namespace Argon.Features.Logging;

using Env;
using Serilog;
using Serilog.Formatting.Json;

public static class LoggingFeature
{
    /// <summary>
    /// Installs Serilog. Whether to call this at all is the caller's decision — the feature owns the
    /// <c>Logging:Structured</c> setting that used to be the <c>NO_STRUCTURED_LOGS</c> variable.
    /// </summary>
    public static WebApplicationBuilder AddLogging(this WebApplicationBuilder builder)
    {
        Log.Logger = new LoggerConfiguration()
           .Enrich.FromLogContext()
           .ReadFrom.Configuration(builder.Configuration)
           .WriteTo.Console(new JsonFormatter(renderMessage: true))
           .CreateLogger();


        AppDomain.CurrentDomain.UnhandledException += (_, args) 
            => Log.Logger.Error(args.ExceptionObject as Exception, "App Crashed");

        builder.Logging
           .AddSerilog();
        builder.Services
           .AddSerilog();

        return builder;
    }
}