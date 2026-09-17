namespace ArgonSharedLogicTest;

using Argon.Features.Clustering;
using Argon.Features.WebSession;
using Microsoft.Extensions.Configuration;

/// <summary>
/// That binding a session shortens the credential, not the session.
/// </summary>
/// <remarks>
/// <para><b>The failure this guards against has already happened once, in production.</b> A bound
/// session's refresh cookie was cut to <c>BoundCookieLifetime</c> — ten minutes — reasoning that a
/// copied cookie should be worth little. But the refresh cookie <i>is</i> the session: cutting it
/// made the session last ten minutes unless the browser renewed it. Renewal is a new browser
/// feature and was then the only thing holding the session up, so a laptop asleep overnight came
/// back signed out, its tab reconnecting for ever, with a perfectly good device key in hand.</para>
///
/// <para>It bought almost nothing, either. That cookie is <c>HttpOnly</c> and <c>__Host-</c>, so
/// script never had it to steal; and ten minutes is ample for malware that has already read the
/// browser profile off disk. The short window only ever reached the honest user.</para>
///
/// <para>So the rule is: what a binding shortens is the credential every call spends — the access
/// token and the cookie carrying it — because a bound browser can quietly obtain another by proving
/// its key. The session's own cookie keeps its full life, and the session survives whether or not
/// the binding ever renews.</para>
/// </remarks>
[TestFixture]
public class BoundSessionLifetimeTests
{
    private static FeatureConfigurationReport Report()
        => new("WebSession", "WebSession", sectionExists: true, role: null,
            new ConfigurationBuilder().Build());

    private static WebSessionOptions Configured(Action<WebSessionOptions>? adjust = null)
    {
        var options = new WebSessionOptions
        {
            TrustedApplications = new(StringComparer.OrdinalIgnoreCase)
            {
                ["A37E7A1DB06E9610C9C0BD77C61A821B"] = ["https://app.argon.gl"],
            },
        };

        adjust?.Invoke(options);

        return options;
    }

    [Test]
    public void ABindingShortensTheCredentialRatherThanLengtheningIt()
    {
        var options = new WebSessionOptions();

        // Otherwise binding a session would make the thing every request carries live LONGER than
        // an unbound one's, which is the opposite of the point of binding it.
        Assert.That(options.DeviceBinding.BoundCookieLifetime,
            Is.LessThanOrEqualTo(options.AccessTokenLifetime));
    }

    /// <summary>
    /// The session outlives its credential by a wide margin. That gap is what a night's sleep falls
    /// into — and closing it is exactly what signed people out.
    /// </summary>
    [Test]
    public void ASessionOutlivesTheCredentialItIssues()
    {
        var options = new WebSessionOptions();

        Assert.That(options.Lifetime, Is.GreaterThan(options.DeviceBinding.BoundCookieLifetime));
        Assert.That(options.Lifetime, Is.GreaterThan(TimeSpan.FromHours(12)),
            "a session shorter than a night's sleep signs people out for sleeping");
    }

    /// <summary>
    /// A bound credential configured longer than an unbound one is a misconfiguration the validator
    /// has to name: the value is otherwise entirely plausible and nothing would fail.
    /// </summary>
    [Test]
    public void TheValidatorObjectsToABindingThatLengthensTheCredential()
    {
        var report = Report();

        Configured(o =>
        {
            o.AccessTokenLifetime = TimeSpan.FromMinutes(5);
            o.DeviceBinding.BoundCookieLifetime = TimeSpan.FromMinutes(30);
        }).Validate(report);

        Assert.That(report.Diagnostics.Select(d => d.Message),
            Has.Some.Contains(nameof(DeviceBindingOptions.BoundCookieLifetime)));
    }

    [Test]
    public void TheValidatorIsQuietAboutTheDefaults()
    {
        var report = Report();

        Configured().Validate(report);

        Assert.That(
            report.Diagnostics.Where(d => d.Message.Contains(nameof(DeviceBindingOptions.BoundCookieLifetime))),
            Is.Empty);
    }
}
