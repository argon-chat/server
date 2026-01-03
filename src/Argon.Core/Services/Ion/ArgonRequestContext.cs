namespace Argon.Services.Ion;

public static class ArgonRequestContext
{
    private static readonly AsyncLocal<ArgonRequestContextData?> _current = new();

    public static ArgonRequestContextData Current
        => _current.Value ?? throw new InvalidOperationException("No active request context");

    public static   void Set(ArgonRequestContextData data) => _current.Value = data;
    internal static void Clear()                           => _current.Value = null;
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


    public IDictionary<string, string> Props         { get; init; } = new Dictionary<string, string>();
    public IClusterClient              ClusterClient => Scope.GetRequiredService<IClusterClient>();
}

public static class ServiceEx
{
    extension(IIonService service)
    {
        public ArgonRequestContextData GetRequestContext() => ArgonRequestContext.Current;
        public IClusterClient          GetClusterClient()  => ArgonRequestContext.Current.ClusterClient;

        public T GetGrain<T>(Guid grainKey) where T : IGrainWithGuidKey
            => ArgonRequestContext.Current.ClusterClient.GetGrain<T>(grainKey);

        public T GetGrain<T>(string grainKey) where T : IGrainWithStringKey
            => ArgonRequestContext.Current.ClusterClient.GetGrain<T>(grainKey);

        public Guid   GetUserId()      => ArgonRequestContext.Current.UserId ?? throw new InvalidOperationException();
        public string GetMachineId()   => ArgonRequestContext.Current.MachineId ?? throw new InvalidOperationException();
        public Guid   GetSessionId()   => ArgonRequestContext.Current.SessionId ?? throw new InvalidOperationException();
        public string GetUserCountry() => ArgonRequestContext.Current.Region;
    }
}