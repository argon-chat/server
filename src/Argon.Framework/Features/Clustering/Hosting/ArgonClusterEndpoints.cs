namespace Argon.Features.Clustering;

/// <summary>
/// Cluster identity and silo ports, resolved from configuration.
/// </summary>
/// <remarks>
/// These were compile-time constants. They are configuration now because one process per role means
/// several silos on one machine — the integration suite boots all eight roles, and local development
/// runs more than one silo at a time.
/// </remarks>
public sealed class ArgonClusterEndpoints
{
    public const string Section = "Argon:Cluster";

    /// <summary>
    /// Identifies one cluster. Every silo in a cluster shares it, and no two clusters may.
    /// </summary>
    /// <remarks>
    /// Per region once there is more than one, because a region is a cluster: two regions sharing a
    /// clustering store and a cluster id would form a single cluster by accident, and a client aimed
    /// at either would reach whichever silo answered. There is no default that could be right for
    /// that case, so a multi-region deployment sets it explicitly and
    /// <c>ArgonRegionOptions</c> checks the value against what the region list says.
    /// </remarks>
    public const string DefaultClusterId = "argon-cluster";

    /// <summary>
    /// Identifies the service, and is deliberately the same everywhere.
    /// </summary>
    /// <remarks>
    /// <para>Orleans keys grain storage and the reminder table on the service id, so it names the
    /// service rather than the deployment — the cluster id names the deployment. Argon had these the
    /// wrong way round: one cluster id for everything and a service id derived from the datacenter,
    /// which meant grain state written in one region was unreachable from another by construction,
    /// and two regions on one clustering store would have merged.</para>
    ///
    /// <para><c>appsettings.json</c> has said <c>Orleans:ServiceId = "argon"</c> all along; that key
    /// never took effect, because the silo builder binds the <c>Orleans</c> section first and then
    /// this class overwrites both properties. The constant here is what that key always meant.</para>
    ///
    /// <para><b>Changing it orphans state.</b> The service id is part of every grain-storage key and
    /// every reminder row, so a deployment that has been running under the old derived value will not
    /// find what it wrote. That is acceptable before release and is not acceptable after.</para>
    /// </remarks>
    public const string DefaultServiceId = "argon";

    public const int DefaultSiloPort = 11111;
    public const int DefaultGateway  = 30000;

    public required string ClusterId   { get; init; }
    public required string ServiceId   { get; init; }
    public required int    SiloPort    { get; init; }
    public required int    GatewayPort { get; init; }

    /// <summary>
    /// Reads the whole set.
    /// </summary>
    /// <remarks>
    /// It used to take the datacenter, and used it for nothing but a service-id default that was
    /// wrong. A parameter that is accepted and ignored is how the old cross-region client factory
    /// came to connect every region to the local cluster, so it is gone rather than left.
    /// </remarks>
    public static ArgonClusterEndpoints Resolve(IConfiguration configuration)
        => new()
        {
            ClusterId   = ClusterIdOf(configuration),
            ServiceId   = ServiceIdOf(configuration),
            SiloPort    = configuration.GetValue($"{Section}:SiloPort", DefaultSiloPort),
            GatewayPort = configuration.GetValue($"{Section}:GatewayPort", DefaultGateway)
        };

    /// <summary>The cluster id this process runs under, resolved the same way the silo resolves it.</summary>
    /// <remarks>
    /// Separate from <see cref="Resolve"/> so a validator can ask the question without needing ports
    /// it does not care about.
    /// </remarks>
    public static string ClusterIdOf(IConfiguration configuration)
        => configuration[$"{Section}:Id"] ?? DefaultClusterId;

    /// <inheritdoc cref="ClusterIdOf"/>
    public static string ServiceIdOf(IConfiguration configuration)
        => configuration[$"{Section}:ServiceId"] ?? DefaultServiceId;
}
