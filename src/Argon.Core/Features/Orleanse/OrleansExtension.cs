namespace Argon.Features;

using Orleans.Hosting;

/// <summary>
/// What is left of the old Orleans hosting layer once the role system took it over.
/// </summary>
/// <remarks>
/// <c>AddWorkerOrleans</c>, <c>AddGatewayOrleans</c>, <c>AddSingleOrleansClient</c>,
/// <c>AddMultiOrleansClient</c> and <c>AddShimsForHybridRole</c> lived here. They branched on
/// <c>ArgonRoleKind</c> and carried three byte-identical copies of the serializer configuration.
/// Their replacement is <see cref="Clustering.ArgonOrleansHosting"/>, which takes the resolved role
/// and configures a silo or a client from it.
/// </remarks>
public static class OrleansExtension
{
    /// <summary>
    /// Registers one Redis-backed grain storage provider per name.
    /// </summary>
    /// <remarks>
    /// It used to take an ADO.NET invariant and a connection string name, and the call site passed
    /// <c>"Npgsql"</c> and <c>"DefaultConnection"</c> — neither was ever read. Grain state has always
    /// gone to Redis, and the signature said Postgres.
    /// </remarks>
    public static ISiloBuilder UseRedisStorages(this ISiloBuilder builder, IEnumerable<string> providerNames)
    {
        foreach (var name in providerNames)
            builder.AddRedisStorage(name);

        return builder;
    }
}
