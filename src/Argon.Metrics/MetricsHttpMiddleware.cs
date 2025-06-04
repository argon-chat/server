namespace Argon.Metrics;

using System.Diagnostics;

public class MetricsHttpMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Processes an HTTP request, measures its execution time, and records request metrics after completion.
    /// </summary>
    /// <param name="context">The current HTTP context for the request.</param>
    /// <param name="collector">The metrics collector used to record request metrics.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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