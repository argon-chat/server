using Argon.Features.Clustering.Regions;

namespace Argon.Services.Ion;

public static class ArgonRequestContext
{
    private static readonly AsyncLocal<ArgonRequestContextData?> _current = new();

    public static ArgonRequestContextData Current
        => _current.Value ?? throw new InvalidOperationException("No active request context");

    public static   void Set(ArgonRequestContextData data) => _current.Value = data;
    internal static void Clear()                           => _current.Value = null;

    public static string LockdownCacheKey(Guid userId) => $"lockdown:{userId}";
}

public sealed class ArgonRequestContextData
{
    public required string           Ip         { get; init; }
    public required string           Region     { get; init; }
    public required string           Ray        { get; init; }
    public required string           ClientName { get; init; }
    public required string?          AppId      { get; init; }
    public required Guid?            SessionId  { get; init; }
    public required string?          MachineId  { get; init; }
    public required Guid?            UserId     { get; init; }
    public required IServiceProvider Scope      { get; init; }

    public LockdownSeverity LockdownSeverity { get; init; }

    public IDictionary<string, string> Props         { get; init; } = new Dictionary<string, string>();
    public IClusterClient              ClusterClient => Scope.GetRequiredService<IClusterClient>();
}

public static class ServiceEx
{
    extension(IIonService service)
    {
        public ArgonRequestContextData GetRequestContext() => ArgonRequestContext.Current;
        public IClusterClient          GetClusterClient()  => ArgonRequestContext.Current.ClusterClient;

        /// <summary>
        /// Resolves a grain, from whichever region owns it.
        /// </summary>
        /// <remarks>
        /// <para>Here because this is where every Ion call in the product turns an id into a grain — one
        /// place, so a routing gap cannot be closed in nineteen services and left open in the
        /// twentieth.</para>
        ///
        /// <para>The local answer costs nothing to reach. <see cref="ForeignRegionCalls.IsForeign"/> is
        /// two process statics and a handful of byte reads, and it is false for every id in a
        /// single-region deployment, so the container is not touched and the registry is not consulted
        /// on the path every request takes.</para>
        ///
        /// <para>A foreign id is routed rather than served, which is what the registry of cluster
        /// clients was built for: the grain reference comes from the owning region's client and Orleans
        /// carries the call there. What it must never do is fall back to the local cluster — that is
        /// the silent failure the whole thing exists to prevent, and it writes rows into the wrong
        /// region's database while looking healthy. So an unusable owner throws.</para>
        /// </remarks>
        public T GetGrain<T>(Guid grainKey) where T : IGrainWithGuidKey
        {
            if (!ForeignRegionCalls.IsForeign(grainKey))
                return ArgonRequestContext.Current.ClusterClient.GetGrain<T>(grainKey);

            var scope    = ArgonRequestContext.Current.Scope;
            var registry = scope.GetRequiredService<IArgonRegionRegistry>();

            if (registry.TryGetClientFor(grainKey, out var owner))
            {
                ForeignRegionCalls.Routed(grainKey, typeof(T).Name);
                return owner.GetGrain<T>(grainKey);
            }

            // Known region, not usable — draining, unreachable, or announced away. GetClient turns that
            // into the exception that names which and why, rather than a bare false.
            ForeignRegionCalls.Unroutable(grainKey, typeof(T).Name,
                scope.GetRequiredService<ILogger<IIonService>>());

            return registry.GetClient(registry.RegionOf(grainKey)).GetGrain<T>(grainKey);
        }

        public T GetGrain<T>(string grainKey) where T : IGrainWithStringKey
            => ArgonRequestContext.Current.ClusterClient.GetGrain<T>(grainKey);

        public Guid    GetUserId()      => ArgonRequestContext.Current.UserId ?? throw new InvalidOperationException();
        public string  GetMachineId()   => ArgonRequestContext.Current.MachineId ?? throw new InvalidOperationException();
        public Guid    GetSessionId()   => ArgonRequestContext.Current.SessionId ?? throw new InvalidOperationException();
        public string? GetClientId()    => ArgonRequestContext.Current.AppId;
        public string  GetUserCountry() => ArgonRequestContext.Current.Region;
        public string? GetUserIp()      => ArgonRequestContext.Current.Ip;

        public void EnforceLockdown(LockdownSeverity minSeverity)
        {
            if (ArgonRequestContext.Current.LockdownSeverity >= minSeverity)
                throw new InvalidOperationException("Account restricted");
        }
    }
}