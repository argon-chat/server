namespace Argon.Metrics;

using System.Diagnostics;

public class MetricsHttpMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context, IMetricsCollector collector)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();

            await collector.MarkRequest(
                route: context.Request.Path,
                status: context.Response.StatusCode,
                elapsed: sw.Elapsed,
                method: context.Request.Method);
        }
    }
}