namespace Argon.Metrics;

public static class GaugeExtensions
{
    /// <summary>
        /// Records a gauge measurement with the specified value and optional tags.
        /// </summary>
        /// <param name="name">The identifier of the gauge metric.</param>
        /// <param name="value">The value to record for the gauge.</param>
        /// <param name="tags">Optional tags to associate with the measurement.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static Task GaugeAsync(this IMetricsCollector collector, MeasurementId name, double value,
        IDictionary<string, string>? tags = null)
        => collector.ObserveAsync(name, value, tags);

    /// <summary>
    /// Temporarily sets a gauge metric to 1 while an asynchronous action executes, then resets it to 0 after completion.
    /// </summary>
    /// <param name="name">The identifier of the gauge metric.</param>
    /// <param name="action">The asynchronous action to execute while the gauge is set to 1.</param>
    /// <param name="tags">Optional tags to associate with the gauge metric.</param>
    /// <remarks>
    /// The gauge is set to 1 before the action starts and is always reset to 0 after the action completes, even if an exception occurs.
    /// </remarks>
    public async static Task WithinGaugeAsync(this IMetricsCollector metrics, MeasurementId name,
        Func<Task> action, IDictionary<string, string>? tags = null)
    {
        await metrics.GaugeAsync(name, 1, tags);
        try
        {
            await action();
        }
        finally
        {
            await metrics.GaugeAsync(name, 0, tags);
        }
    }
}