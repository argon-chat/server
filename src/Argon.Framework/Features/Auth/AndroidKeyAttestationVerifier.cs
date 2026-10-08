namespace Argon.Features.Auth;

using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

public sealed class AndroidAttestationOptions
{
    /// <summary>
    /// Google's hardware attestation root certificates, PEM, from
    /// <c>developer.android.com/privacy-and-security/security-key-attestation</c>.
    /// </summary>
    /// <remarks>
    /// Configuration rather than a constant in the source on purpose: this is the trust anchor for
    /// the whole mechanism, Google publishes more than one and rotates them, and a value transcribed
    /// from memory into a source file is exactly the sort of thing that is wrong in a way nobody
    /// notices until every Android device is refused — or, worse, until one is wrongly accepted.
    /// </remarks>
    public string[] RootCertificatesPem { get; set; } = [];

    /// <summary>Refuse keys the device would not put in the TEE. StrongBox is stricter still.</summary>
    public bool RequireStrongBox { get; set; }
}

/// <summary>
/// Verifies Android key attestation: a certificate chain in which Google states that the key was
/// generated inside the device's secure hardware.
/// </summary>
/// <remarks>
/// <para>This is the strongest signal of the three platforms, and the only one where the statement
/// comes from someone other than the device itself. The chain runs from the attestation key's leaf
/// up to a Google root, and the leaf carries an extension describing the key: where it lives, what
/// it may be used for, and — crucially — the challenge it was created for.</para>
///
/// <para>The challenge check is what makes it a proof rather than a recording. Without it a chain
/// captured from any genuine device could be replayed by anyone, since nothing else in the blob is
/// specific to this enrolment.</para>
/// </remarks>
public sealed class AndroidKeyAttestationVerifier(
    IOptions<AndroidAttestationOptions> options,
    AndroidAttestationRoots roots,
    ILogger<AndroidKeyAttestationVerifier> logger) : IDeviceAttestationVerifier
{
    /// <summary>The KeyDescription extension Google puts on the attestation leaf.</summary>
    private const string AttestationOid = "1.3.6.1.4.1.11129.2.1.17";

    public DevicePlatform Platform => DevicePlatform.ANDROID;

    public AttestationVerdict Verify(string publicKeySpki, string nonce, string? attestation)
    {
        if (string.IsNullOrWhiteSpace(attestation))
            return AttestationVerdict.Unattested("android: no chain offered");

        // Fetched from Google rather than configured, so a fresh deployment needs nothing set up.
        // An empty set means we cannot judge — which is not the same as judging and refusing, so it
        // downgrades rather than rejecting; see AndroidAttestationRoots.
        var anchors = roots.GetAsync().GetAwaiter().GetResult();

        if (anchors.Length == 0)
        {
            logger.LogError("No Android attestation roots available; accepting keys as KEY rather than ATTESTED");

            return AttestationVerdict.Unattested("android: no roots available");
        }


        try
        {
            var chain = ParseChain(attestation);

            if (chain.Count < 2)
                return AttestationVerdict.Rejected("android: chain too short to reach a root");

            var leaf = chain[0];

            // The attested key must be the key being enrolled. Without this the caller could send a
            // genuine chain for one key and register a different one they hold in software.
            if (!SameKey(leaf, publicKeySpki))
                return AttestationVerdict.Rejected("android: leaf does not carry the enrolled key");

            if (!LinksUp(chain))
                return AttestationVerdict.Rejected("android: chain does not link");

            if (!ReachesTrustedRoot(chain, anchors))
                return AttestationVerdict.Rejected("android: chain does not reach a pinned Google root");

            var description = leaf.Extensions[AttestationOid];

            if (description is null)
                return AttestationVerdict.Rejected("android: leaf carries no attestation extension");

            var parsed = ReadKeyDescription(description.RawData);

            if (parsed is null)
                return AttestationVerdict.Rejected("android: unreadable attestation extension");

            // Replay defence: the chain has to have been produced for this enrolment.
            if (!CryptographicOperations.FixedTimeEquals(
                    parsed.Value.Challenge, System.Text.Encoding.ASCII.GetBytes(nonce)))
                return AttestationVerdict.Rejected("android: attestation challenge does not match");

            // 1 = TEE, 2 = StrongBox. 0 is software, which is the case this whole class exists to
            // separate from the others.
            if (parsed.Value.SecurityLevel == 0)
                return AttestationVerdict.Rejected("android: key is software-backed");

            if (options.Value.RequireStrongBox && parsed.Value.SecurityLevel != 2)
                return AttestationVerdict.Rejected("android: StrongBox required, key is TEE-only");

            return AttestationVerdict.Attested(
                $"android: security level {parsed.Value.SecurityLevel}, attestation v{parsed.Value.Version}");
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Android attestation could not be parsed");
            return AttestationVerdict.Rejected("android: malformed attestation");
        }
    }

    private static List<X509Certificate2> ParseChain(string attestation)
        => attestation
           .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .Select(der => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(der)))
           .ToList();

    private static bool SameKey(X509Certificate2 leaf, string publicKeySpki)
    {
        var attested = leaf.PublicKey.ExportSubjectPublicKeyInfo();

        return CryptographicOperations.FixedTimeEquals(attested, Convert.FromBase64String(publicKeySpki));
    }

    /// <summary>Each certificate must actually be signed by the next one up.</summary>
    /// <remarks>
    /// Checked by hand rather than with <see cref="X509Chain"/> because the attestation leaves are
    /// routinely outside their validity window on devices with a wrong clock, and an expiry is not a
    /// reason to disbelieve a statement about where a key lives.
    /// </remarks>
    private static bool LinksUp(List<X509Certificate2> chain)
    {
        for (var i = 0; i < chain.Count - 1; i++)
        {
            var child  = chain[i];
            var issuer = chain[i + 1];

            if (!child.IssuerName.RawData.SequenceEqual(issuer.SubjectName.RawData))
                return false;

            if (!IsSignedBy(child, issuer))
                return false;
        }

        return true;
    }

    private static bool IsSignedBy(X509Certificate2 child, X509Certificate2 issuer)
    {
        using var ecdsa = issuer.GetECDsaPublicKey();

        if (ecdsa is not null)
            return child.SignatureAlgorithm.Value is not null &&
                   ecdsa.VerifyData(child.RawDataMemory.Span[..GetTbsLength(child)], GetSignature(child),
                       HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        using var rsa = issuer.GetRSAPublicKey();

        return rsa is not null &&
               rsa.VerifyData(child.RawDataMemory.Span[..GetTbsLength(child)], GetSignature(child),
                   HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    // The signed region of a certificate is its tbsCertificate, which is the first element of the
    // outer SEQUENCE; the signature is the third.
    private static int GetTbsLength(X509Certificate2 certificate)
    {
        var outer = new AsnReader(certificate.RawDataMemory, AsnEncodingRules.DER).ReadSequence();
        var tbs   = outer.ReadEncodedValue();

        return tbs.Length;
    }

    private static byte[] GetSignature(X509Certificate2 certificate)
    {
        var outer = new AsnReader(certificate.RawDataMemory, AsnEncodingRules.DER).ReadSequence();

        outer.ReadEncodedValue();                    // tbsCertificate
        outer.ReadSequence();                        // signatureAlgorithm

        return outer.ReadBitString(out _);           // signatureValue
    }

    /// <summary>
    /// Whether the top of the chain is one of Google's roots.
    /// </summary>
    /// <remarks>
    /// Compared by public key rather than by thumbprint, so a root re-issued with the same key still
    /// anchors — which is what a certificate renewal looks like and is not a reason to refuse a fleet.
    /// </remarks>
    private static bool ReachesTrustedRoot(List<X509Certificate2> chain, X509Certificate2[] anchors)
    {
        var top = chain[^1].PublicKey.ExportSubjectPublicKeyInfo();

        foreach (var anchor in anchors)
        {
            if (CryptographicOperations.FixedTimeEquals(top, anchor.PublicKey.ExportSubjectPublicKeyInfo()))
                return true;
        }

        return false;
    }

    private readonly record struct KeyDescription(int Version, int SecurityLevel, byte[] Challenge);

    /// <summary>
    /// Reads the fields this class judges on out of the KeyDescription SEQUENCE.
    /// </summary>
    /// <remarks>
    /// Only the first five members are read — version, security level, keymaster version and level,
    /// then the challenge. The authorisation lists after them describe what the key may be used for
    /// and are not what is being decided here.
    /// </remarks>
    private static KeyDescription? ReadKeyDescription(byte[] extension)
    {
        var octet = new AsnReader(extension, AsnEncodingRules.DER).ReadOctetString();
        var body  = new AsnReader(octet, AsnEncodingRules.DER).ReadSequence();

        if (!body.TryReadInt32(out var attestationVersion)) return null;
        if (!body.TryReadInt32(out var attestationSecurityLevel)) return null;
        if (!body.TryReadInt32(out _)) return null;   // keymasterVersion
        if (!body.TryReadInt32(out _)) return null;   // keymasterSecurityLevel

        var challenge = body.ReadOctetString();

        return new KeyDescription(attestationVersion, attestationSecurityLevel, challenge);
    }
}
