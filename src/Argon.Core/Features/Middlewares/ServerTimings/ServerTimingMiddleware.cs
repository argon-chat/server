namespace Argon.Features.Middlewares;

public sealed class ServerTimingMiddleware(RequestDelegate next, IOptions<ServerTimingOptions> options)
{
    private const string HeaderName = "Server-Timing";

    private readonly RequestDelegate     next    = next ?? throw new System.ArgumentNullException(nameof(next));
    private readonly ServerTimingOptions options = options?.Value ?? new ServerTimingOptions();

    public Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        context.Response.OnStarting(() =>
        {
            var metrics = this.GetMetrics(context, stopwatch).ToList();
            if (metrics.Count != 0)
                context.Response.Headers[HeaderName] = string.Join(",", metrics);

            return Task.CompletedTask;
        });

        return this.next(context);
    }

    /// <summary>
    /// Converts record to a metric for the response header.
    /// See: https://www.w3.org/TR/server-timing/#the-server-timing-header-field
    /// </summary>
    /// <param name="record">Record</param>
    /// <returns>Metric</returns>
    private static string GetMetric(Record record)
    {
        var builder = new StringBuilder(record.Name);
        if (!string.IsNullOrWhiteSpace(record.Description))
            builder.Append($";desc=\"{record.Description}\"");

        if (record.Duration.HasValue)
            builder.Append($";dur={record.Duration}");

        return builder.ToString();
    }

    /// <summary>
    /// Combines recorded and custom metrics.
    /// </summary>
    /// <param name="context">Http context</param>
    /// <param name="stopwatch">Stopwatch</param>
    /// <returns>Metrics</returns>
    private IEnumerable<string> GetMetrics(HttpContext context, Stopwatch stopwatch)
    {
        var recorder = context.RequestServices.GetService<IServerTimingRecorder>();
        if (recorder != null)
        {
            foreach (var record in recorder.GetRecords())
            {
                yield return GetMetric(record);
            }
        }

        if (this.options.TotalIncluded)
        {
            yield return GetMetric(new Record(
                this.options.TotalName,
                this.options.TotalDescription,
                stopwatch.ElapsedMilliseconds));
        }
    }
}