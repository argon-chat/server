namespace Argon.Features.Auth;

using System.Security.Cryptography;
using Argon.Services;
using System.Text;

/// <param name="PublicKey">SubjectPublicKeyInfo, base64. P-256.</param>
/// <param name="IssuedAt">When the client signed. Bounds how long the proof is worth anything.</param>
/// <param name="Signature">Base64 signature over <c>{issuedAt}|{machineId}</c>.</param>
/// <param name="Attestation">
/// The platform's own statement about where the key lives, when it has one. Present only on the
/// first request from a machine — it is a certificate chain, and re-sending it on every call would
/// put kilobytes on the wire to say something the server already recorded.
/// </param>
public sealed record DeviceProof(string PublicKey, long IssuedAt, string Signature, string? Attestation);

/// <summary>
/// Reads and checks the device proof that native code pushes into the <c>ArgonSecure</c> cookie.
/// </summary>
/// <remarks>
/// <para>There is no challenge round trip, because there is no service to ask: the cookie exists so
/// native code can carry device identity, and adding an RPC for it would put hardware into a
/// contract that has no business knowing what a TPM is.</para>
///
/// <para>Freshness therefore comes from a timestamp rather than a server nonce, and that trade has
/// to be paid for honestly: a signature is accepted only inside <see cref="Window"/>, and every one
/// that is accepted is remembered for the rest of that window so it cannot be presented twice. A
/// captured proof is then worth at most one use inside a minute, on a request that also has to
/// carry the same machine id it was signed against.</para>
///
/// <para>The machine id is signed over, not merely sent, so a proof lifted from another machine's
/// cookie does not verify against this one's — otherwise the whole thing would be a bearer value
/// again, which is exactly what <c>colt</c> already is.</para>
/// </remarks>
public class DeviceProofVerifier(IArgonCacheDatabase cache, ILogger<DeviceProofVerifier> logger)
{
    /// <summary>
    /// How far out of date a proof may be.
    /// </summary>
    /// <remarks>
    /// Wide enough to survive a phone whose clock drifted and a slow request, narrow enough that a
    /// captured proof is stale before it can be carried anywhere useful. Applied in both directions,
    /// because a clock can be fast as easily as slow.
    /// </remarks>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private static string SeenKey(string signature)
        => $"device:proof:{Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(signature)))[..32]}";

    /// <summary>
    /// Parses the <c>dev</c> field of the cookie: <c>publicKey.issuedAt.signature[.attestation]</c>.
    /// </summary>
    /// <remarks>
    /// Unparseable input yields null rather than throwing. This runs for every caller, including
    /// clients too old to send the field at all, and a malformed proof means the server learns
    /// nothing about the machine — never that the request is refused.
    /// </remarks>
    public static DeviceProof? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var parts = raw.Split('.');

        if (parts.Length is < 3 or > 4)
            return null;

        if (!long.TryParse(parts[1], out var issuedAt))
            return null;

        return new DeviceProof(parts[0], issuedAt, parts[2], parts.Length == 4 ? parts[3] : null);
    }

    /// <summary>
    /// Whether this proof was made by the key it names, recently, for this machine, and only once.
    /// </summary>
    public async Task<bool> VerifyAsync(DeviceProof proof, string machineId, CancellationToken ct = default)
    {
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(proof.IssuedAt);

        if (age > Window || age < -Window)
            return false;

        if (!VerifySignature(proof, machineId))
            return false;

        try
        {
            var key = SeenKey(proof.Signature);

            if (await cache.KeyExistsAsync(key, ct))
                return false;

            // Remembered for a whole window rather than until the proof expires: the two are the
            // same length, and keying off the signature means the entry can be dropped by time
            // alone without tracking which machine it belonged to.
            await cache.StringSetAsync(key, "1", Window, ct);

            return true;
        }
        catch (Exception e)
        {
            // Fails closed. A proof that cannot be confirmed unused is a proof that can be replayed,
            // and the cost of refusing is that the caller is treated as having no device — not that
            // the request is rejected.
            logger.LogError(e, "Could not check a device proof for replay");
            return false;
        }
    }

    /// <summary>
    /// P-256 with SHA-256, over <c>{issuedAt}|{machineId}</c>.
    /// </summary>
    /// <remarks>
    /// That is the only curve all three hardware stores offer without argument — Android Keystore,
    /// the Secure Enclave and a TPM through CNG — and the Enclave offers nothing else.
    /// </remarks>
    public static bool VerifySignature(DeviceProof proof, string machineId)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(proof.PublicKey), out _);

            return ecdsa.VerifyData(
                Encoding.ASCII.GetBytes($"{proof.IssuedAt}|{machineId}"),
                Convert.FromBase64String(proof.Signature),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception)
        {
            // Every byte came from the caller, so a malformed proof is a failed proof rather than a
            // fault worth propagating into the auth path.
            return false;
        }
    }

    /// <summary>Rejects anything that is not a P-256 public key before it reaches storage.</summary>
    public static bool IsAcceptablePublicKey(string publicKeySpki)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);

            return ecdsa.KeySize == 256;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Stable identifier for a key, used to name a device without carrying the key.</summary>
    public static string Thumbprint(string publicKeySpki)
        => Convert.ToBase64String(SHA256.HashData(Convert.FromBase64String(publicKeySpki)))
                  .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
