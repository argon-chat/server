namespace Argon.Features.Orleanse.Storages;

using Orleans.Runtime.Hosting;
using Orleans.Storage;

/// <summary>
/// A grain storage provider that stores nothing.
/// </summary>
/// <remarks>
/// <para>It exists so a grain can hold in-memory state as <see cref="IPersistentState{T}"/> without
/// that state ever reaching a database. The point is not the storage — there is none — it is what
/// Orleans does with an <c>IPersistentState</c> when an activation migrates: its
/// <c>StateStorageBridge</c> is itself an <c>IGrainMigrationParticipant</c>, so it hands the whole
/// state object to the runtime on the way out and marks it initialised on the way in, which also
/// skips the read on the far side. Declaring the state is the whole of the work; nothing has to be
/// packed or unpacked by hand.</para>
///
/// <para>The alternative would have been the memory storage provider, and it is a different thing
/// entirely: that one keeps state in a grain, so every read and write is a call across the cluster.
/// Here a read is a completed task, which is what makes it free to put on the activation path of a
/// grain that already loads real state from Redis.</para>
///
/// <para>State kept here does not survive the activation ending — only its migration. Anything that
/// must outlive a silo going down belongs in a real provider, and asking this one to write refuses
/// rather than pretending.</para>
/// </remarks>
public sealed class VolatileGrainStorage : IGrainStorage
{
    /// <summary>The provider name grains name in <c>[PersistentState]</c>.</summary>
    public const string ProviderName = "volatile";

    /// <summary>
    /// Reports that there is nothing stored, which is true.
    /// </summary>
    /// <remarks>
    /// The one operation that has to succeed quietly: the runtime calls it on every activation, before
    /// any grain code runs, and a throw here would mean the grain could not activate at all. A
    /// migrated activation never reaches it — its state bridge has already marked the state
    /// initialised and skips the read.
    /// </remarks>
    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        grainState.RecordExists = false;
        grainState.ETag         = null;
        return Task.CompletedTask;
    }

    /// <summary>Refuses. Writing to storage that stores nothing is a mistake, not a no-op.</summary>
    /// <remarks>
    /// Nothing in the runtime calls this — only grain code does, and only deliberately. Accepting it
    /// silently would make <c>await state.WriteStateAsync()</c> look like persistence and behave like
    /// a discarded write, which is the kind of data loss that shows up as a support ticket months
    /// later rather than as a failure. It costs nothing to make it impossible instead.
    /// </remarks>
    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => throw new NotSupportedException(
            $"'{stateName}' on grain '{grainId}' is held in {nameof(VolatileGrainStorage)}, which stores " +
            "nothing: it exists so in-memory state survives a migration, not a deactivation. Either drop " +
            "the write, or move this state to a provider that persists.");

    /// <inheritdoc cref="WriteStateAsync{T}"/>
    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => throw new NotSupportedException(
            $"'{stateName}' on grain '{grainId}' is held in {nameof(VolatileGrainStorage)}, which has " +
            "nothing to clear. Reset the state object instead.");
}

public static class VolatileStorageExtensions
{
    /// <summary>Registers the storage that stores nothing, under <see cref="VolatileGrainStorage.ProviderName"/>.</summary>
    public static ISiloBuilder AddVolatileStorage(this ISiloBuilder builder)
        => builder.ConfigureServices(services => services.AddGrainStorage(
            VolatileGrainStorage.ProviderName, static (_, _) => new VolatileGrainStorage()));
}
