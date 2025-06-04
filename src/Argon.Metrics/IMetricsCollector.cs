namespace Argon.Metrics;

public interface IMetricsCollector
{
    /// <summary>
/// Records a count metric for the specified measurement identifier asynchronously.
/// </summary>
/// <param name="measurement">The identifier for the metric to record.</param>
/// <param name="value">The count value to record. Defaults to 1.</param>
/// <param name="tags">Optional key-value pairs to associate with the metric.</param>
/// <returns>A task representing the asynchronous operation.</returns>
Task CountAsync(MeasurementId measurement, long value = 1, IDictionary<string, string>? tags = null);
    /// <summary>
/// Records an observation metric with the specified measurement identifier, value, and optional tags.
/// </summary>
/// <param name="measurement">The identifier for the metric to observe.</param>
/// <param name="value">The observed value to record.</param>
/// <param name="tags">Optional key-value pairs to further describe the metric context.</param>
/// <returns>A task representing the asynchronous operation.</returns>
Task ObserveAsync(MeasurementId measurement, double value, IDictionary<string, string>? tags = null);
    /// <summary>
/// Records a duration metric for the specified measurement identifier.
/// </summary>
/// <param name="measurement">The identifier for the metric to record.</param>
/// <param name="duration">The duration value to record.</param>
/// <param name="tags">Optional tags to associate with the metric.</param>
/// <returns>A task representing the asynchronous operation.</returns>
Task DurationAsync(MeasurementId measurement, TimeSpan duration, IDictionary<string, string>? tags = null);
}

public readonly record struct MeasurementId(string key)
{
    public static readonly MeasurementId HttpRequests = new("http_requests");
    public static readonly MeasurementId Exceptions   = new("exceptions");
    public static readonly MeasurementId ActiveUsers  = new("active_users");
    public static readonly MeasurementId UserOnline   = new("user_online");

    public static class Dotnet
    {
        public static class Gc
        {
            public static readonly MeasurementId TotalMemory     = new("dotnet_gc_total_memory_mb");
            public static readonly MeasurementId Gen0Collections = new("dotnet_gc_gen0_collections");
            public static readonly MeasurementId Gen1Collections = new("dotnet_gc_gen1_collections");
            public static readonly MeasurementId Gen2Collections = new("dotnet_gc_gen2_collections");
        }

        public static class ThreadPool
        {
            public static readonly MeasurementId WorkerAvailable = new("dotnet_threadpool_worker_available");
            public static readonly MeasurementId IOAvailable     = new("dotnet_threadpool_io_available");
            public static readonly MeasurementId WorkerMin       = new("dotnet_threadpool_worker_min");
            public static readonly MeasurementId WorkerMax       = new("dotnet_threadpool_worker_max");
            public static readonly MeasurementId IOMin           = new("dotnet_threadpool_io_max");
            public static readonly MeasurementId IOMax           = new("dotnet_threadpool_io_max");
        }
    }
}