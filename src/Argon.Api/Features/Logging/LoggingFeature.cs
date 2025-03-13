namespace Argon.Features.Logging;

using Env;
using Serilog;
using Serilog.Formatting.Json;

public static class LoggingFeature
{
    public static WebApplicationBuilder AddLogging(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsSingleInstance()) 
            return builder;

        Log.Logger = new LoggerConfiguration()
           .Enrich.FromLogContext()
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