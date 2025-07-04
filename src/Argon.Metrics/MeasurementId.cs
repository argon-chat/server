namespace Argon.Metrics;

public readonly record struct MeasurementId(string key)
{
    public static readonly MeasurementId HttpRequests = new("http_requests");
    public static readonly MeasurementId Exceptions   = new("exceptions");
    public static readonly MeasurementId ActiveUsers  = new("active_users");
    public static readonly MeasurementId UserOnline   = new("user_online");

    public static readonly MeasurementId GrainCallTiming = new("grain_call_timing");


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
            public static readonly MeasurementId IOMin           = new("dotnet_threadpool_io_min");
            public static readonly MeasurementId IOMax           = new("dotnet_threadpool_io_max");
        }
    }

    public static class RedisMetrics
    {
        public static readonly MeasurementId PoolAllocated    = new("redis_pool_allocated");
        public static readonly MeasurementId PoolTaken        = new("redis_pool_taken");
        public static readonly MeasurementId PoolScaleUp      = new("redis_pool_scale_up");
        public static readonly MeasurementId PoolCleanup      = new("redis_pool_cleanup");
        public static readonly MeasurementId PoolCreateError  = new("redis_pool_create_error");
        public static readonly MeasurementId PoolCleanupError = new("redis_pool_cleanup_error");
    }
}