namespace Argon.Metrics;

public interface IMetricsCollector
{
    Task CountAsync(MeasurementId measurement, long value = 1, IDictionary<string, string>? tags = null);
    Task ObserveAsync(MeasurementId measurement, double value, IDictionary<string, string>? tags = null);
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