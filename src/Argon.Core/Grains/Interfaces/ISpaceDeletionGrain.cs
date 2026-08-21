namespace Argon.Grains.Interfaces;

using ArgonContracts;

/// <summary>
/// The wait between "delete this space" and the space being gone.
/// </summary>
/// <remarks>
/// Deletion is scheduled rather than immediate, and the delay is the feature: a space holds other
/// people's history, so everyone in it is told and given time to save what matters, and the owner
/// gets the same window to change their mind. A community waits longer than a private space for the
/// same reason — more people are living there.
/// </remarks>
[Alias($"Argon.Grains.Interfaces.{nameof(ISpaceDeletionGrain)}")]
public interface ISpaceDeletionGrain : IGrainWithGuidKey
{
    [Alias(nameof(RequestAsync))]
    Task<(SpaceDeletionError error, SpaceDeletionState state)> RequestAsync(Guid callerId);

    [Alias(nameof(CancelAsync))]
    Task<SpaceDeletionError> CancelAsync(Guid callerId);

    [Alias(nameof(GetStateAsync))]
    Task<SpaceDeletionState> GetStateAsync();

    /// <summary>Runs the deletion if its moment has passed. Idempotent; safe to call on a timer.</summary>
    [Alias(nameof(CheckAndExecuteAsync))]
    Task CheckAndExecuteAsync();
}
