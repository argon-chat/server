namespace Argon.Grains.Interfaces;

/// <summary>
/// The machines behind hardware keys, and whether they are barred.
/// </summary>
/// <remarks>
/// Stateless, keyed by <see cref="Guid.Empty"/>. The Ion layer asks on every refresh that carries a
/// device proof and on every request from a bound session, and holds no database of its own.
/// </remarks>
[Alias("Argon.Grains.Interfaces.IDeviceIdentityGrain")]
public interface IDeviceIdentityGrain : IGrainWithGuidKey
{
    /// <summary>
    /// The machine behind a public key the caller has already verified a proof from, recording it the
    /// first time it is seen. Null when the machine is barred or cannot be resolved.
    /// </summary>
    [Alias(nameof(ResolveByKeyAsync))]
    Task<Guid?> ResolveByKeyAsync(Guid userId, string publicKey, CancellationToken ct = default);

    [Alias(nameof(IsBannedAsync))]
    Task<bool> IsBannedAsync(Guid deviceId, CancellationToken ct = default);
}
