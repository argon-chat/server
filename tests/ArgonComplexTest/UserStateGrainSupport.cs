namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Providers;
using Orleans.Storage;

/// <summary>
/// What the user-state fixtures need from the silo beyond its Ion surface: a grain's own persisted
/// state, and a way to end one activation.
/// </summary>
/// <remarks>
/// <para>The level and stats grains keep a hot copy in grain storage and a durable one in the
/// database, and most of what can go wrong between the two happens at an activation boundary — a day
/// that ended while the grain was asleep, a cache that lost its copy, a record that went missing
/// underneath a live grain. Reaching those boundaries means seeding the store before an activation
/// reads it, and ending an activation on purpose, which is what this is for.</para>
///
/// <para>The store is written the way <c>AccountDeletionTests</c> writes it: read first for the
/// ETag, because the provider refuses a blind write over an existing record.</para>
/// </remarks>
internal static class UserStateGrainSupport
{
    private static IServiceProvider Services => ArgonTestEnvironment.Instance.Host.Services;

    private static IGrainFactory Grains => Services.GetRequiredService<IGrainFactory>();

    private static IGrainStorage Store
        => Services.GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);

    /// <summary>The grain's persisted state as the store holds it, or a default one if there is none.</summary>
    public static async Task<T> ReadStateAsync<T>(string stateName, IAddressable grain) where T : new()
    {
        var state = new GrainState<T>(new T());

        await Store.ReadStateAsync(stateName, grain.GetGrainId(), state);

        return state.State ?? new T();
    }

    /// <summary>Writes the grain's persisted state, as an earlier activation would have left it.</summary>
    public static async Task WriteStateAsync<T>(string stateName, IAddressable grain, T seed) where T : new()
    {
        var state = new GrainState<T>(new T());

        await Store.ReadStateAsync(stateName, grain.GetGrainId(), state);

        state.State = seed;

        await Store.WriteStateAsync(stateName, grain.GetGrainId(), state);
    }

    /// <summary>Drops the grain's persisted state, as a cache that lost it would.</summary>
    public static async Task ClearStateAsync<T>(string stateName, IAddressable grain) where T : new()
    {
        var state = new GrainState<T>(new T());

        await Store.ReadStateAsync(stateName, grain.GetGrainId(), state);
        await Store.ClearStateAsync(stateName, grain.GetGrainId(), state);
    }

    /// <summary>Asks the activation to deactivate once idle — the same request a drain makes.</summary>
    public static async Task DeactivateAsync(IAddressable grain)
        => await Grains.GetGrain<IGrainManagementExtension>(grain.GetGrainId()).DeactivateOnIdle();

    /// <summary>Whether the cluster currently holds an activation of this grain anywhere.</summary>
    public static async Task<bool> IsActiveAsync(IAddressable grain)
    {
        var id         = grain.GetGrainId();
        var statistics = await Grains.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics();

        return statistics.Any(s => s.GrainId.Equals(id));
    }

    /// <summary>
    /// Deactivates the grain and waits until the cluster no longer lists it, so the next call is known
    /// to land on a fresh activation.
    /// </summary>
    public static async Task<bool> DeactivateAndWaitAsync(IAddressable grain, TimeSpan timeout, CancellationToken ct)
    {
        await DeactivateAsync(grain);

        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (!await IsActiveAsync(grain))
                return true;

            await Task.Delay(50, ct);
        }

        return false;
    }
}
