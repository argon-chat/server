namespace Argon.Features.Auth;

using Argon.Features.Clustering;

/// <summary>
/// How a password is turned into something safe to store.
/// </summary>
public enum PasswordHashAlgorithm
{
    LegacySha256,
    Pbkdf2HmacSha256,
    Pbkdf2HmacSha512
}

public sealed class PasswordHashingOptions : IValidatableFeatureOptions
{
    public PasswordHashAlgorithm Algorithm { get; set; } = PasswordHashAlgorithm.Pbkdf2HmacSha512;

    /// <summary>
    /// Passes over the password.
    /// </summary>
    /// <remarks>
    /// This is the whole cost, and it is a cost on purpose. OWASP's figure for PBKDF2-HMAC-SHA-512 is
    /// 210,000; this sits below it deliberately, because at that setting a node measured 156
    /// registrations a second against 450 before any of this, and the login path pays the same on
    /// every sign-in. What the salt already bought is the larger half — no shared work across
    /// accounts, so an attacker with the whole table must pay this per password rather than once.
    /// <para>
    /// Raising it later is safe and is the intended response to faster hardware: existing digests
    /// carry the count they were made with, still verify against it, and are rewritten at the owner's
    /// next successful login.
    /// </para>
    /// </remarks>
    public int Iterations { get; set; } = 40_000;

    public int SaltBytes { get; set; } = 16;

    public int HashBytes { get; set; } = 64;

    public void Validate(IFeatureConfigurationReport report)
    {
        if (Algorithm == PasswordHashAlgorithm.LegacySha256)
            report.Invalid($"{nameof(Algorithm)} cannot be {nameof(PasswordHashAlgorithm.LegacySha256)}: it is " +
                           "unsalted and single-pass, and exists only so existing accounts can sign in once more");

        report.RequireRange(Iterations, 10_000, 5_000_000, nameof(Iterations));
        report.RequireRange(SaltBytes, 16, 64, nameof(SaltBytes));
        report.RequireRange(HashBytes, 32, 128, nameof(HashBytes));
    }
}
