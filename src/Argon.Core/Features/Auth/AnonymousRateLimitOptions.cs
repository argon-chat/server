namespace Argon.Features.Auth;

using Argon.Features.Clustering;

/// <summary>
/// Per-IP windows on the anonymous identity surface — the calls anyone can make without a token.
/// </summary>
/// <remarks>
/// Deliberately generous and short-windowed: this gate sits in front of every login, so a shared
/// office address or a CGNAT range must not be able to lock its users out. The tight,
/// credential-specific limits live per e-mail inside <c>IdentityInteraction</c>.
/// <para>
/// Configuration rather than constants because the numbers are a deployment decision, and because a
/// load run registers its crowd from one address and would otherwise trip the guard at the twenty-
/// first user with a "Bad Request" that says nothing about why.
/// </para>
/// </remarks>
public sealed class AnonymousRateLimitOptions : IValidatableFeatureOptions
{
    /// <summary>Turning this off removes the guard entirely. For a load run against a private host.</summary>
    public bool Enabled { get; set; } = true;

    public RateWindow Authorize                   { get; set; } = new(100, TimeSpan.FromMinutes(5));
    public RateWindow Registration                { get; set; } = new(20, TimeSpan.FromMinutes(10));
    public RateWindow BeginResetPassword          { get; set; } = new(15, TimeSpan.FromMinutes(15));
    public RateWindow ResetPassword               { get; set; } = new(30, TimeSpan.FromMinutes(10));
    public RateWindow GetAuthorizationScenarioFor { get; set; } = new(60, TimeSpan.FromMinutes(5));

    /// <summary>The window for a method, or <c>null</c> when it is not credential-bearing and stays open.</summary>
    public RateWindow? For(string methodName) => methodName switch
    {
        nameof(Authorize)                   => Authorize,
        nameof(Registration)                => Registration,
        nameof(BeginResetPassword)          => BeginResetPassword,
        nameof(ResetPassword)               => ResetPassword,
        nameof(GetAuthorizationScenarioFor) => GetAuthorizationScenarioFor,
        _                                   => null
    };

    public void Validate(IFeatureConfigurationReport report)
    {
        if (!Enabled)
        {
            report.Prefer(false, nameof(Enabled),
                "is off, so the anonymous login surface has no per-IP guard at all");
            return;
        }

        foreach (var (name, window) in new (string, RateWindow)[]
                 {
                     (nameof(Authorize), Authorize),
                     (nameof(Registration), Registration),
                     (nameof(BeginResetPassword), BeginResetPassword),
                     (nameof(ResetPassword), ResetPassword),
                     (nameof(GetAuthorizationScenarioFor), GetAuthorizationScenarioFor)
                 })
        {
            report.Require(window.Max > 0, name, "must allow at least one attempt per window");
            report.Require(window.Window > TimeSpan.Zero, name, "needs a window longer than zero");
        }
    }
}

/// <summary>How many attempts are allowed, and over what span.</summary>
public sealed class RateWindow
{
    public RateWindow()
    {
    }

    public RateWindow(int max, TimeSpan window)
    {
        Max    = max;
        Window = window;
    }

    public int      Max    { get; set; }
    public TimeSpan Window { get; set; }
}
