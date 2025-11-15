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
    public required string            Ip         { get; init; }
    public required string            Region     { get; init; }
    public required string            Ray        { get; init; }
    public required string            ClientName { get; init; }
    public required string            HostName   { get; init; }
    public required string            AppId      { get; init; }
    public required Guid              SessionId  { get; init; }
    public required string            MachineId  { get; init; }
    public required Guid?             UserId     { get; init; }
    public required IServiceProvider Scope      { get; init; }


    public IDictionary<string, string> Props         { get; init; } = new Dictionary<string, string>();
    public IClusterClient              ClusterClient => Scope.GetRequiredService<IClusterClient>();
}

public static class ServiceEx
{
    public static ArgonRequestContextData GetRequestContext(this IIonService service) => ArgonRequestContext.Current;
    public static IClusterClient          GetClusterClient(this IIonService service)  => ArgonRequestContext.Current.ClusterClient;

    public static T GetGrain<T>(this IIonService service, Guid grainKey) where T : IGrainWithGuidKey
        => ArgonRequestContext.Current.ClusterClient.GetGrain<T>(grainKey);

    public static T GetGrain<T>(this IIonService service, string grainKey) where T : IGrainWithStringKey
        => ArgonRequestContext.Current.ClusterClient.GetGrain<T>(grainKey);

    public static Guid   GetUserId(this IIonService service)      => ArgonRequestContext.Current.UserId ?? throw new InvalidOperationException();
    public static string GetMachineId(this IIonService service)   => ArgonRequestContext.Current.MachineId ?? throw new InvalidOperationException();
    public static Guid   GetSessionId(this IIonService service)   => ArgonRequestContext.Current.SessionId;
    public static string GetUserCountry(this IIonService service) => ArgonRequestContext.Current.Region;
}