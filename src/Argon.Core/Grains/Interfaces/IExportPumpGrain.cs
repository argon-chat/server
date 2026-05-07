namespace Argon.Grains.Interfaces;

/// <summary>
/// Singleton grain that periodically pings active export grains to ensure they stay alive.
/// Export grains use RegisterGrainTimer which dies on deactivation — this pump reactivates them.
/// </summary>
[Alias($"Argon.Grains.Interfaces.{nameof(IExportPumpGrain)}")]
public interface IExportPumpGrain : IGrainWithGuidKey
{
    static readonly Guid SingletonId = Guid.Parse("a0a0a0a0-e7e7-cafe-0000-000000000002");

    /// <summary>
    /// Register a user export as active. Called when export starts.
    /// </summary>
    [Alias(nameof(RegisterActiveExportAsync))]
    ValueTask RegisterActiveExportAsync(Guid userId);

    /// <summary>
    /// Unregister a user export. Called on completion, failure, or cancellation.
    /// </summary>
    [Alias(nameof(UnregisterExportAsync))]
    ValueTask UnregisterExportAsync(Guid userId);
}
