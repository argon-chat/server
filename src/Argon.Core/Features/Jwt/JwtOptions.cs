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

    public required TimeSpan AccessTokenLifetime { get; set; }
}

public record KeyPair(string privateKey, string publicKey, string? password);

public sealed class WrapperForSignKey
{
    public SecurityKey PrivateKey { get; }
    public SecurityKey PublicKey  { get; }
    public string      Algorithm  { get; }

    public WrapperForSignKey(IOptions<JwtOptions> options)
    {
        var jwt = options.Value.CertificateBase64;

        if (jwt is null || string.IsNullOrWhiteSpace(jwt.privateKey) || string.IsNullOrWhiteSpace(jwt.publicKey))
            throw new InvalidOperationException("JwtOptions: both PrivateKey and PublicKey must be specified.");

        PrivateKey = LoadKey(jwt.privateKey, jwt.password, isPrivate: true);
        PublicKey  = LoadKey(jwt.publicKey, jwt.password, isPrivate: false);

        Algorithm = GetDefaultAlgorithm(PrivateKey);
    }

    private static SecurityKey LoadKey(string input, string? password, bool isPrivate)
    {
        input = input.Trim();
        // PEM
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

    private static string GetDefaultAlgorithm(SecurityKey key) => key switch
    {
        ECDsaSecurityKey                                                        => SecurityAlgorithms.EcdsaSha256,
        RsaSecurityKey                                                          => SecurityAlgorithms.RsaSha256,
        X509SecurityKey x509 when x509.Certificate.GetECDsaPrivateKey() != null => SecurityAlgorithms.EcdsaSha256,
        X509SecurityKey x509 when x509.Certificate.GetRSAPrivateKey() != null   => SecurityAlgorithms.RsaSha256,
        _                                                                       => SecurityAlgorithms.RsaSha256
    };
}

public enum TokenValidationError
{
    BAD_TOKEN,
    EXPIRED_TOKEN
}