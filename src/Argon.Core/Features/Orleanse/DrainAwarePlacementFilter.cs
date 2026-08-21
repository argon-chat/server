namespace Argon.Features;

using Drains;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

/// <summary>
/// Placement filter strategy that excludes draining silos from placement decisions.
/// Applied globally to all grains to support blue-green deployments.
/// </summary>
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
        return services;
    }
}
