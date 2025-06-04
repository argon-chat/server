namespace Argon.Metrics;

public static class CountExtensions
{
    /// <summary>
        /// Increments the specified metric by a given value asynchronously.
        /// </summary>
        /// <param name="name">The identifier of the metric to increment.</param>
        /// <param name="value">The amount to increment the metric by. Defaults to 1.</param>
        /// <param name="tags">Optional tags to associate with the metric.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static Task CountAsync(this IMetricsCollector collector, MeasurementId name, long value = 1,
        IDictionary<string, string>? tags = null)
        => collector.CountAsync(name, value, tags);

    /// <summary>
        /// Increments the specified metric by one asynchronously.
        /// </summary>
        /// <param name="name">The identifier of the metric to increment.</param>
        /// <param name="tags">Optional tags to associate with the metric.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static Task IncAsync(this IMetricsCollector collector, MeasurementId name,
        IDictionary<string, string>? tags = null)
        => collector.CountAsync(name, 1, tags);
}