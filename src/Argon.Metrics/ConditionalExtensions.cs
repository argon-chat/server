namespace Argon.Metrics;

public static class ConditionalExtensions
{
    /// <summary>
    /// Increments a metric count asynchronously if the specified synchronous condition evaluates to true.
    /// </summary>
    /// <param name="name">The identifier of the metric to increment.</param>
    /// <param name="condition">A synchronous delegate that determines whether to increment the count.</param>
    /// <param name="value">The amount to increment the metric by. Defaults to 1.</param>
    /// <param name="tags">Optional tags to associate with the metric.</param>
    public async static Task CountIfAsync(this IMetricsCollector collector, MeasurementId name, Func<bool> condition,
        long value = 1, IDictionary<string, string>? tags = null)
    {
        if (condition())
            await collector.CountAsync(name, value, tags);
    }

    /// <summary>
    /// Conditionally increments a metric count asynchronously if the provided asynchronous condition evaluates to true.
    /// </summary>
    /// <param name="name">The identifier of the metric to increment.</param>
    /// <param name="condition">An asynchronous function that returns true if the metric should be incremented.</param>
    /// <param name="value">The amount to increment the metric by. Defaults to 1.</param>
    /// <param name="tags">Optional tags to associate with the metric.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async static Task CountIfAsync(this IMetricsCollector collector, MeasurementId name, Func<Task<bool>> condition,
        long value = 1, IDictionary<string, string>? tags = null)
    {
        if (await condition())
            await collector.CountAsync(name, value, tags);
    }
}