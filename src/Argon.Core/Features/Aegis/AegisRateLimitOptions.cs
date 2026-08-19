namespace Argon.Features.Aegis;

using Argon.Features.Clustering;

/// <summary>
/// One window's worth of rate limit.
/// </summary>
public sealed class RateLimitWindow
{
    public int      Permits { get; set; }
    public TimeSpan Window  { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// What one address may do to the identity server per minute.
/// </summary>
/// <remarks>
/// Three limits because the endpoints are three different things to an attacker. <see cref="Auth"/>
/// covers the ones that check a credential, and is the tightest — that is where guessing happens.
/// <see cref="Token"/> covers redemption, which a legitimate client does a handful of times per
/// sign-in. <see cref="Global"/> is the backstop over everything else on the role, partitioned by
/// remote address.
/// </remarks>
public sealed class AegisRateLimitOptions : IValidatableFeatureOptions
{
    public const string SectionName = "AegisRateLimits";

    /// <summary>Policy name the credential-checking endpoints carry.</summary>
    public const string AuthPolicy  = "auth";

    /// <summary>Policy name the token and machine-flow endpoints carry.</summary>
    public const string TokenPolicy = "token";

    public RateLimitWindow Auth   { get; set; } = new() { Permits = 5, Window = TimeSpan.FromMinutes(1) };
    public RateLimitWindow Token  { get; set; } = new() { Permits = 30, Window = TimeSpan.FromMinutes(1) };
    public RateLimitWindow Global { get; set; } = new() { Permits = 100, Window = TimeSpan.FromMinutes(1) };

    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.SectionExists)
            return;

        Check(Auth, nameof(Auth));
        Check(Token, nameof(Token));
        Check(Global, nameof(Global));

        void Check(RateLimitWindow window, string name)
        {
            report.RequireRange(window.Permits, 1, 1_000_000, $"{name}:Permits");
            report.RequireRange(window.Window, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), $"{name}:Window");
        }
    }
}
