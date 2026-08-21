namespace Argon.Features.Auth;


/// <param name="Assurance">What the platform was willing to vouch for.</param>
/// <param name="Detail">Short, loggable summary of why. Never shown to the client.</param>
public readonly record struct AttestationVerdict(DeviceAssurance Assurance, string Detail)
{
    public static AttestationVerdict Unattested(string why) => new(DeviceAssurance.KEY, why);
    public static AttestationVerdict Attested(string detail) => new(DeviceAssurance.ATTESTED, detail);

    /// <summary>An attestation blob was offered and did not hold up — refuse rather than downgrade.</summary>
    public static AttestationVerdict Rejected(string why) => new(DeviceAssurance.NONE, why);
}

/// <summary>
/// Checks a platform's statement that a key really lives in hardware.
/// </summary>
/// <remarks>
/// <para>One implementation per platform, because the statements have nothing in common: Android
/// hands over an X.509 chain rooted in Google's attestation root with the key's properties in an
/// extension, Apple hands over a CBOR attestation object, and a TPM hands over a quote against an
/// endorsement key whose certificate chains to the chip vendor.</para>
///
/// <para>A verifier that cannot check a blob must answer <see cref="AttestationVerdict.Rejected"/>
/// rather than falling back to <see cref="DeviceAssurance.KEY"/>. Downgrading silently would make a
/// forged attestation strictly better than sending none — the forger would land on the same level
/// as an honest client while looking like they tried.</para>
/// </remarks>
public interface IDeviceAttestationVerifier
{
    DevicePlatform Platform { get; }

    /// <param name="nonce">The challenge, which must appear inside the attestation itself.</param>
    AttestationVerdict Verify(string publicKeySpki, string nonce, string? attestation);
}

/// <summary>
/// The verifier for platforms that have no attestation to offer.
/// </summary>
/// <remarks>
/// Desktop Windows without a usable TPM, Linux, and anything else that can hold a key but cannot
/// prove where. Still worth enrolling: a key the client keeps is what stops a copied cookie from
/// working elsewhere, which is the jump from tier zero to tier one even with nobody vouching.
/// </remarks>
public sealed class UnattestedDeviceVerifier(DevicePlatform platform) : IDeviceAttestationVerifier
{
    public DevicePlatform Platform { get; } = platform;

    public AttestationVerdict Verify(string publicKeySpki, string nonce, string? attestation)
        => string.IsNullOrWhiteSpace(attestation)
            ? AttestationVerdict.Unattested($"{Platform}: no attestation offered")
            // Something was sent that this platform has no way to check. Accepting it as KEY would
            // let a client claim a level nobody verified.
            : AttestationVerdict.Rejected($"{Platform}: attestation offered but unverifiable here");
}
