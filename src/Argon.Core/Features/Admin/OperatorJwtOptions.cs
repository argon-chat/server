namespace Argon.Features.Admin;

public record OperatorJwtOptions
{
    public const string SectionName = "OperatorJwt";

    public required string Issuer   { get; set; }
    public required string Audience { get; set; }
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// EC or RSA key pair for signing operator JWT tokens.
    /// Separate from user JWT keys.
    /// Format: PEM or Base64-encoded DER.
    /// </summary>
    public required KeyPairConfig SigningKey { get; set; }

    public record KeyPairConfig(string PrivateKey, string PublicKey);
}
