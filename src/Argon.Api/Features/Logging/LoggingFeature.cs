namespace Argon.Features.Logging;

using Env;
using Serilog;
using Serilog.Formatting.Json;

public static class LoggingFeature
{
    public static WebApplicationBuilder AddLogging(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsKube())
        {
            Log.Logger = new LoggerConfiguration()
               .Enrich.FromLogContext()
               .WriteTo.Console(new JsonFormatter(renderMessage: true))
               .CreateLogger();

            builder.Logging
               .AddSerilog();
            builder.Services
               .AddSerilog();
        }

        return builder;
    }
}