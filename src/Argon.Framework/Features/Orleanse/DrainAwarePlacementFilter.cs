namespace Argon.Features;

using Drains;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

/// <summary>
/// Placement filter strategy that excludes draining silos from placement decisions.
/// </summary>
/// <remarks>
/// Applied to every grain type by <see cref="DrainAwarePlacementFilterProvider"/> rather than by an
/// attribute. Registering a filter is not enough on its own: Orleans resolves filters per grain type
/// from the <c>PlacementFilter</c> grain property, which the attribute exists to populate, and a
/// resolver that finds no such property returns none. This filter was registered and attached to
/// nothing for exactly that reason.
/// </remarks>
[Serializable, GenerateSerializer, Immutable]
public sealed class DrainAwarePlacementFilterStrategy() : PlacementFilterStrategy(-100);

/// <summary>
/// Placement filter director that removes the local silo from placement candidates
/// when it is in draining state. This prevents new grain activations from being
/// created on a silo that is shutting down.
/// </summary>
public class DrainAwarePlacementFilterDirector(
    ISiloDrainService drainService,
    ILocalSiloDetails localSiloDetails,
    ILogger<DrainAwarePlacementFilterDirector> logger) : IPlacementFilterDirector
{
    private bool _loggedDrainFilter;

    public IEnumerable<SiloAddress> Filter(
        PlacementFilterStrategy filterStrategy,
        PlacementTarget target,
        IEnumerable<SiloAddress> silos)
    {
        var status = drainService.GetStatus();

        if (status.State == SiloDrainState.Active)
            return silos;

        // When draining/drained/shutting down - exclude local silo from candidates
        var localSilo     = localSiloDetails.SiloAddress;
        var siloAddresses = silos as SiloAddress[] ?? silos.ToArray();
        var filtered      = siloAddresses.Where(s => !s.Equals(localSilo)).ToList();

        if (!_loggedDrainFilter)
        {
            logger.LogWarning(
                "Drain-aware placement filter active. Local silo {LocalSilo} (state: {State}) excluded from placement. " +
                "Candidates reduced from {OriginalCount} to {FilteredCount}",
                localSilo, status.State, siloAddresses.Count(), filtered.Count);
            _loggedDrainFilter = true;
        }

        if (filtered.Count != 0) 
            return filtered;
        logger.LogCritical(
            "No silos available after drain filter. Local silo {LocalSilo} is the only option but is draining.",
            localSilo);
        return [];

    }
}

/// <summary>
/// Puts the drain-aware filter on every grain type, without annotating a single one.
/// </summary>
/// <remarks>
/// <para>The documented way to apply a placement filter is an attribute on the grain class. That does
/// not scale to "all of them": forty-odd classes to annotate, and the one added next week is the one
/// that keeps activating on a silo being taken out of service.</para>
///
/// <para>The attribute is only a way of writing a grain property, and grain properties have another
/// author — <see cref="IGrainPropertiesProvider"/>, which the manifest asks about every grain type it
/// builds. Writing the property here reaches every grain, including ones that do not exist yet.</para>
/// </remarks>
public sealed class DrainAwarePlacementFilterProvider(IServiceProvider services) : IGrainPropertiesProvider
{
    public void Populate(Type grainClass, GrainType grainType, Dictionary<string, string> properties)
        => new DrainAwarePlacementFilterStrategy().PopulateGrainProperties(services, grainClass, grainType, properties);
}

/// <summary>
/// Extension methods for registering drain-aware placement filter.
/// </summary>
public static class DrainAwarePlacementExtensions
{
    /// <summary>
    /// Adds the drain-aware placement filter that prevents grain activations
    /// on silos that are draining for blue-green deployments.
    /// </summary>
    public static IServiceCollection AddDrainAwarePlacementFilter(this IServiceCollection services)
    {
        services.AddPlacementFilter<DrainAwarePlacementFilterStrategy, DrainAwarePlacementFilterDirector>(
            ServiceLifetime.Singleton);

        // Registering the filter only makes it resolvable. This is what makes it apply.
        services.AddSingleton<IGrainPropertiesProvider, DrainAwarePlacementFilterProvider>();

        return services;
    }
}
