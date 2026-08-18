namespace Argon.Features.Jwt;

using Argon.Features.Clustering;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Token issuing. The <c>required</c> members are checked against configuration, so a section that
/// omits one is reported by name instead of producing a token nobody can validate.
/// </summary>
public record JwtOptions : IValidatableFeatureOptions
{
    public required string Issuer { get; set; }

    public required string Audience { get; set; }

    /// <summary>Mixed into the per-device machine id. Changing it invalidates every device binding.</summary>
    public required string MachineSalt { get; set; }


    /// <summary>
    /// PEM, Base64(PFX), Base64(CER), Base64(PUB/PRIV)
    /// </summary>
    public KeyPair? CertificateBase64 { get; set; }

    public RsaKeyPair? EncryptionBase64 { get; set; }

    public required TimeSpan AccessTokenLifetime { get; set; }

    public void Validate(IFeatureConfigurationReport report)
    {
        report.Require(AccessTokenLifetime > TimeSpan.Zero, nameof(AccessTokenLifetime),
            "must be positive, or every token is born expired");

        // WrapperForSignKey throws on a missing pair, and it is constructed lazily — the first login
        // after a bad deploy is where you would find out otherwise.
        report.Require(CertificateBase64 is { privateKey.Length: > 0, publicKey.Length: > 0 },
            nameof(CertificateBase64), "must carry both a private and a public key; nothing can be signed without them");
    }
}

public record RsaKeyPair(string PrivateKeyBase64, string PublicKeyBase64);
public record KeyPair(string privateKey, string publicKey, string? password);

public sealed class WrapperForEncryptionKey
{
    public SecurityKey PrivateKey { get; }
    public SecurityKey PublicKey  { get; }
    public string      Kid        { get; }

    public WrapperForEncryptionKey(IOptions<JwtOptions> options)
    {
        var jwt = options.Value;

        var pair = jwt.EncryptionBase64
            ?? throw new InvalidOperationException("JwtOptions: EncryptionBase64 must be specified for encryption.");

        PrivateKey = LoadRsaKey(pair.PrivateKeyBase64, isPrivate: true);
        PublicKey  = LoadRsaKey(pair.PublicKeyBase64, isPrivate: false);

        Kid              = ComputeKid(PublicKey);
        PrivateKey.KeyId = Kid;
        PublicKey.KeyId  = Kid;
    }

    private static SecurityKey LoadRsaKey(string input, bool isPrivate)
    {
        var raw = Convert.FromBase64String(input);
        var rsa = RSA.Create();

        if (isPrivate)
            rsa.ImportRSAPrivateKey(raw, out _);
        else
            rsa.ImportSubjectPublicKeyInfo(raw, out _);

        return new RsaSecurityKey(rsa);
    }

    private static string ComputeKid(SecurityKey key)
    {
        using var sha = SHA256.Create();
        var       rsa = (RsaSecurityKey)key;
        var       p   = rsa.Rsa.ExportParameters(false);

        var data = new byte[p.Modulus!.Length + p.Exponent!.Length];
        Buffer.BlockCopy(p.Modulus, 0, data, 0, p.Modulus.Length);
        Buffer.BlockCopy(p.Exponent, 0, data, p.Modulus.Length, p.Exponent.Length);

        var hash = sha.ComputeHash(data);
        return Base64UrlEncoder.Encode(hash);
    }
}

/// <summary>
/// The key tokens are signed with, and one instance of it per thread that does the signing.
/// </summary>
/// <remarks>
/// A single shared key cannot be signed with concurrently. <c>ECDsaCng</c> and <c>RSACng</c> wrap a
/// Windows CNG handle, and <c>SignHash</c> on the same instance from several threads is not merely
/// unsynchronised — it takes the process down inside <c>NCryptSignHash</c>, with no exception to
/// catch and no stack in managed code. Four hundred simultaneous registrations was enough, and every
/// path that issues a token — sign-in, refresh, QR approval — reaches the same key.
/// <para>
/// One instance per thread rather than a lock, because signing is the expensive part of issuing a
/// token and serialising it would put the whole node behind one core. Thread-pool threads are
/// reused and bounded, so the number of live keys is bounded with them. The material is imported
/// once and kept, so a new thread's key costs an import rather than a key generation.
/// </para>
/// </remarks>
public sealed class WrapperForSignKey : IDisposable
{
    private readonly ThreadLocal<SecurityKey> signingKeys;

    /// <summary>
    /// A key for signing, private to the calling thread.
    /// </summary>
    /// <remarks>
    /// Never hold this beyond the signing call, and never hand it to another thread: what makes it
    /// safe is that only one thread ever touches it.
    /// </remarks>
    public SecurityKey PrivateKey => signingKeys.Value!;

    public SecurityKey PublicKey { get; }
    public string Algorithm { get; }
    public string Kid { get; }

    public WrapperForSignKey(IOptions<JwtOptions> options)
    {
        var jwt = options.Value.CertificateBase64;

        if (jwt is null || string.IsNullOrWhiteSpace(jwt.privateKey) || string.IsNullOrWhiteSpace(jwt.publicKey))
            throw new InvalidOperationException("JwtOptions: both PrivateKey and PublicKey must be specified.");

        // Once, so that a bad key fails at startup rather than on the first login, and so the
        // algorithm and kid below are derived from the same material every thread will import.
        var probe = LoadKey(jwt.privateKey, jwt.password, isPrivate: true);

        PublicKey = LoadKey(jwt.publicKey, jwt.password, isPrivate: false);

        Algorithm = GetDefaultAlgorithm(probe);
        Kid       = ComputeKid(PublicKey);

        PublicKey.KeyId = Kid;

        // Every thread's key carries the same kid, so the token header names the key whatever thread
        // signed it and JWKS validation still finds it.
        signingKeys = new ThreadLocal<SecurityKey>(() =>
        {
            var key = LoadKey(jwt.privateKey, jwt.password, isPrivate: true);
            key.KeyId = Kid;
            return key;
        }, trackAllValues: true);
    }

    public void Dispose()
    {
        foreach (var key in signingKeys.Values)
            Release(key);

        signingKeys.Dispose();
    }

    private static void Release(SecurityKey key)
    {
        switch (key)
        {
            case ECDsaSecurityKey ec:
                ec.ECDsa.Dispose();
                break;
            case RsaSecurityKey rsa:
                rsa.Rsa.Dispose();
                break;
        }
    }

    private static string ComputeKid(SecurityKey key)
    {
        using var sha = SHA256.Create();

        if (key is ECDsaSecurityKey ec)
        {
            var p = ec.ECDsa.ExportParameters(false);

            // concat X || Y
            var data = new byte[p.Q.X!.Length + p.Q.Y!.Length];
            Buffer.BlockCopy(p.Q.X, 0, data, 0, p.Q.X.Length);
            Buffer.BlockCopy(p.Q.Y, 0, data, p.Q.X.Length, p.Q.Y.Length);

            var hash = sha.ComputeHash(data);
            return Base64UrlEncoder.Encode(hash);
        }

        if (key is RsaSecurityKey rsa)
        {
            var p = rsa.Rsa.ExportParameters(false);

            // concat N || E
            var data = new byte[p.Modulus!.Length + p.Exponent!.Length];
            Buffer.BlockCopy(p.Modulus, 0, data, 0, p.Modulus.Length);
            Buffer.BlockCopy(p.Exponent, 0, data, p.Modulus.Length, p.Exponent.Length);

            var hash = sha.ComputeHash(data);
            return Base64UrlEncoder.Encode(hash);
        }

        throw new NotSupportedException($"Unsupported key type: {key.GetType().Name}");
    }

    private static SecurityKey LoadKey(string input, string? password, bool isPrivate)
    {
        input = input.Trim();
        if (input.Contains("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            if (input.Contains("EC", StringComparison.OrdinalIgnoreCase))
            {
                var ec = ECDsa.Create();
                ec.ImportFromPem(input.AsSpan());
                return new ECDsaSecurityKey(ec);
            }

            var rsa = RSA.Create();
            rsa.ImportFromPem(input.AsSpan());
            return new RsaSecurityKey(rsa);
        }

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(input);
        }
        catch
        {
            throw new InvalidOperationException("Invalid Base64 key or certificate data.");
        }
        try
        {
        #pragma warning disable SYSLIB0057
            var cert = new X509Certificate2(
                raw,
                password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
        #pragma warning restore SYSLIB0057

            if (isPrivate && !cert.HasPrivateKey)
                throw new InvalidOperationException("PFX certificate does not contain a private key.");

            return new X509SecurityKey(cert);
        }
        catch (CryptographicException)
        { } // skip

        try
        {
            if (isPrivate)
            {
                var rsa = RSA.Create();
                rsa.ImportRSAPrivateKey(raw, out _);
                return new RsaSecurityKey(rsa);
            }
            else
            {
                var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(raw, out _);
                return new RsaSecurityKey(rsa);
            }
        }
        catch (CryptographicException)
        {
            try
            {
                if (isPrivate)
                {
                    var ec = ECDsa.Create();
                    ec.ImportECPrivateKey(raw, out _);
                    return new ECDsaSecurityKey(ec);
                }
                else
                {
                    var ec = ECDsa.Create();
                    ec.ImportSubjectPublicKeyInfo(raw, out _);
                    return new ECDsaSecurityKey(ec);
                }
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("Unknown key or certificate format. Supported: PEM, Base64(PFX), Base64(CER), Base64(DER).");
            }
        }
    }

    private static SecurityKey LoadRsaFromBase64(string input, bool isPrivate)
    {
        var raw = Convert.FromBase64String(input);

        var rsa = RSA.Create();

        if (isPrivate)
            rsa.ImportPkcs8PrivateKey(raw, out _);
        else
            rsa.ImportSubjectPublicKeyInfo(raw, out _);

        return new RsaSecurityKey(rsa);
    }

    private static string GetDefaultAlgorithm(SecurityKey key) => key switch
    {
        ECDsaSecurityKey => SecurityAlgorithms.EcdsaSha256,
        RsaSecurityKey => SecurityAlgorithms.RsaSha256,
        X509SecurityKey x509 when x509.Certificate.GetECDsaPrivateKey() != null => SecurityAlgorithms.EcdsaSha256,
        X509SecurityKey x509 when x509.Certificate.GetRSAPrivateKey() != null => SecurityAlgorithms.RsaSha256,
        _ => SecurityAlgorithms.RsaSha256
    };
}
public enum TokenValidationError
{
    BAD_TOKEN,
    EXPIRED_TOKEN
}