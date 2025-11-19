namespace Argon.Features.Jwt;

using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography.X509Certificates;

public record JwtOptions
{
    public required string Issuer { get; set; }

    public required string Audience { get; set; }

    public required string MachineSalt { get; set; }


    /// <summary>
    /// PEM, Base64(PFX), Base64(CER), Base64(PUB/PRIV)
    /// </summary>
    public KeyPair? CertificateBase64 { get; set; }

    public RsaKeyPair? EncryptionBase64 { get; set; }

    public required TimeSpan AccessTokenLifetime { get; set; }


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

        var data = new byte[p.Modulus.Length + p.Exponent.Length];
        Buffer.BlockCopy(p.Modulus, 0, data, 0, p.Modulus.Length);
        Buffer.BlockCopy(p.Exponent, 0, data, p.Modulus.Length, p.Exponent.Length);

        var hash = sha.ComputeHash(data);
        return Base64UrlEncoder.Encode(hash);
    }
}

public sealed class WrapperForSignKey
{
    public SecurityKey PrivateKey { get; }
    public SecurityKey PublicKey { get; }
    public string Algorithm { get; }
    public string Kid { get; }

    public WrapperForSignKey(IOptions<JwtOptions> options)
    {
        var jwt = options.Value.CertificateBase64;

        if (jwt is null || string.IsNullOrWhiteSpace(jwt.privateKey) || string.IsNullOrWhiteSpace(jwt.publicKey))
            throw new InvalidOperationException("JwtOptions: both PrivateKey and PublicKey must be specified.");

        PrivateKey = LoadKey(jwt.privateKey, jwt.password, isPrivate: true);
        PublicKey = LoadKey(jwt.publicKey, jwt.password, isPrivate: false);

        Algorithm = GetDefaultAlgorithm(PrivateKey);

        Kid = ComputeKid(PublicKey);

        PublicKey.KeyId  = Kid;
        PrivateKey.KeyId = Kid;
    }

    private static string ComputeKid(SecurityKey key)
    {
        using var sha = SHA256.Create();

        if (key is ECDsaSecurityKey ec)
        {
            var p = ec.ECDsa.ExportParameters(false);

            // concat X || Y
            var data = new byte[p.Q.X.Length + p.Q.Y.Length];
            Buffer.BlockCopy(p.Q.X, 0, data, 0, p.Q.X.Length);
            Buffer.BlockCopy(p.Q.Y, 0, data, p.Q.X.Length, p.Q.Y.Length);

            var hash = sha.ComputeHash(data);
            return Base64UrlEncoder.Encode(hash);
        }

        if (key is RsaSecurityKey rsa)
        {
            var p = rsa.Rsa.ExportParameters(false);

            // concat N || E
            var data = new byte[p.Modulus.Length + p.Exponent.Length];
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
            var cert = new X509Certificate2(
                raw,
                password,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);

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