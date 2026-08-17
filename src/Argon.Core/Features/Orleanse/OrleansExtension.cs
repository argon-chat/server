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
    public static ISiloBuilder UseStorages(this ISiloBuilder builder, List<string> keys, string invariant, string connString)
    {
        foreach (var key in keys)
            builder.AddRedisStorage(key);

        return builder;
    }
}
