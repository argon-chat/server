namespace Argon.Features.Clustering;

/// <summary>
/// Cluster identity and silo ports, resolved from configuration.
/// </summary>
/// <remarks>
/// These were compile-time constants. They are configuration now because one process per role means
/// several silos on one machine — the integration suite boots all eight roles, and local development
/// runs more than one silo at a time. The defaults are what the single hard-coded silo used, so an
/// existing deployment that sets nothing behaves exactly as before.
/// </remarks>
public sealed class ArgonClusterEndpoints
{
    public const string Section = "Argon:Cluster";

    public const string DefaultClusterId = "argon-cluster";
    public const int    DefaultSiloPort  = 11111;
    public const int    DefaultGateway   = 30000;

    public required string ClusterId   { get; init; }
    public required string ServiceId   { get; init; }
    public required int    SiloPort    { get; init; }
    public required int    GatewayPort { get; init; }

    public static ArgonClusterEndpoints Resolve(IConfiguration configuration, string datacenter)
        => new()
        {
            ClusterId   = configuration[$"{Section}:Id"] ?? DefaultClusterId,
            ServiceId   = configuration[$"{Section}:ServiceId"] ?? $"argon-region-{datacenter}",
            SiloPort    = configuration.GetValue($"{Section}:SiloPort", DefaultSiloPort),
            GatewayPort = configuration.GetValue($"{Section}:GatewayPort", DefaultGateway)
        };
}
