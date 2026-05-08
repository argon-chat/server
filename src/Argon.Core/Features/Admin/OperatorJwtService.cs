namespace Argon.Features.Admin;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

public record OperatorTokenData(Guid OperatorId, string Email, string CertificateThumbprint);

public sealed class OperatorJwtService
{
    private readonly OperatorJwtOptions _options;
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validationParameters;

    public OperatorJwtService(IOptions<OperatorJwtOptions> options)
    {
        _options = options.Value;

        var privateKey = LoadKey(_options.SigningKey.PrivateKey, isPrivate: true);
        var publicKey  = LoadKey(_options.SigningKey.PublicKey, isPrivate: false);

        var algorithm = privateKey is ECDsaSecurityKey
            ? SecurityAlgorithms.EcdsaSha256
            : SecurityAlgorithms.RsaSha256;

        _signingCredentials = new SigningCredentials(privateKey, algorithm);

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = _options.Issuer,
            ValidateAudience         = true,
            ValidAudience            = _options.Audience,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = publicKey,
            ClockSkew                = TimeSpan.FromSeconds(30)
        };
    }

    public string GenerateToken(OperatorTokenData data)
    {
        var now = DateTime.UtcNow;

        var claims = new[]
        {
            new Claim("sub", data.OperatorId.ToString()),
            new Claim("email", data.Email),
            new Claim("typ", "operator"),
            new Claim("cert_tp", data.CertificateThumbprint)
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: now + _options.AccessTokenLifetime,
            signingCredentials: _signingCredentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public OperatorTokenData? ValidateToken(string token)
    {
        try
        {
            var handler    = new JwtSecurityTokenHandler();
            var principal  = handler.ValidateToken(token, _validationParameters, out _);

            var typClaim = principal.FindFirst("typ")?.Value;
            if (typClaim != "operator")
                return null;

            var sub     = principal.FindFirst("sub")?.Value;
            var email   = principal.FindFirst("email")?.Value;
            var certTp  = principal.FindFirst("cert_tp")?.Value;

            if (sub is null || email is null || certTp is null)
                return null;

            if (!Guid.TryParse(sub, out var operatorId))
                return null;

            return new OperatorTokenData(operatorId, email, certTp);
        }
        catch
        {
            return null;
        }
    }

    private static SecurityKey LoadKey(string input, bool isPrivate)
    {
        input = input.Trim();

        // PEM format
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

        // Base64 DER
        var raw = Convert.FromBase64String(input);

        try
        {
            var ec = ECDsa.Create();
            if (isPrivate)
                ec.ImportECPrivateKey(raw, out _);
            else
                ec.ImportSubjectPublicKeyInfo(raw, out _);
            return new ECDsaSecurityKey(ec);
        }
        catch
        {
            var rsa = RSA.Create();
            if (isPrivate)
                rsa.ImportRSAPrivateKey(raw, out _);
            else
                rsa.ImportSubjectPublicKeyInfo(raw, out _);
            return new RsaSecurityKey(rsa);
        }
    }
}
