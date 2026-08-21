namespace Argon.Features.Auth;

/// <summary>
/// Hardware identity vocabulary, deliberately outside the ion contract.
/// </summary>
/// <remarks>
/// None of this belongs on the wire as an RPC surface. Device identity arrives the way it always
/// has — pushed into the <c>ArgonSecure</c> cookie by native code, which is what that cookie was
/// created for — and is read on the auth path rather than asked for over a service. Keeping the
/// types here means the contract never learns what a TPM is, and a client cannot call a device
/// method because there is none to call.
/// </remarks>
public enum DevicePlatform
{
    UNKNOWN,
    WINDOWS,
    ANDROID,
    MACOS,
    IOS,
    LINUX
}

/// <summary>How much the server knows about where a device key lives, rather than how much it wants to.</summary>
public enum DeviceAssurance
{
    /// <summary>
    /// No key — the hardware fingerprint and nothing else. Spoofable by anyone willing to edit a
    /// cookie; kept because Linux and the web have nothing better.
    /// </summary>
    NONE,

    /// <summary>
    /// A key the client says is hardware-backed. Stops a copied cookie, because the signature cannot
    /// be produced elsewhere, but nothing has confirmed the key is not simply held in software.
    /// </summary>
    KEY,

    /// <summary>
    /// The platform signed a statement that this key is in real hardware on a genuine device:
    /// Android key attestation chaining to Google's root, or Apple App Attest.
    /// </summary>
    ATTESTED
}
