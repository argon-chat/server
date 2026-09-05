namespace ArgonSharedLogicTest.Aegis;

using Argon.Api.Features.AccountConsole;

/// <summary>
/// What a client app that runs on somebody's device is allowed to register as its redirect.
/// </summary>
/// <remarks>
/// None of these reach the network. The private-use cases never touch the rule that dials a host,
/// and the web cases are refused by a rule that runs before it — deliberately, because a test that
/// depends on a TLS handshake is a test that fails on an aeroplane.
/// </remarks>
[TestFixture]
public class NativeRedirectValidationTests
{
    private static Task<string?> Validate(string redirect)
        => NativeAppRedirectValidator.ForNativeApps().ValidateAsync(redirect);

    [Test]
    public async Task A_private_use_scheme_naming_a_domain_backwards_is_accepted()
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await Validate("gl.argon.app://oauth/callback"), Is.Null);
            Assert.That(await Validate("gl.argon.app:/callback"), Is.Null,
                "the path-only form is the one RFC 8252 actually recommends");
            Assert.That(await Validate("gl.argon.app://callback"), Is.Null,
                "and a redirect that is nothing but a scheme and a host is still a redirect");
        });
    }

    /// <summary>
    /// The reverse-domain rule is the whole of what stops an application from claiming a scheme
    /// somebody else's software already answers.
    /// </summary>
    [Test]
    public async Task A_bare_scheme_is_refused()
        => Assert.That(await Validate("myapp://callback"), Does.Contain("reverse order"));

    [Test]
    public async Task The_schemes_a_browser_treats_specially_stay_forbidden()
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await Validate("javascript://x.y/"), Does.Contain("forbidden"));
            Assert.That(await Validate("file://x.y/etc/passwd"), Does.Contain("forbidden"));
            Assert.That(await Validate("data://x.y/"), Does.Contain("forbidden"));
        });
    }

    /// <summary>
    /// Being native buys a second way home, not an exemption from the rules about the first one.
    /// </summary>
    [Test]
    public async Task A_web_address_is_still_held_to_the_web_rules()
        => Assert.That(await Validate("https://app.test.local/callback"), Does.Contain("'local' is forbidden"),
            "'.local' is refused for a native app for the same reason it is refused for a bot");

    [Test]
    public async Task A_loopback_address_is_accepted()
        => Assert.That(await Validate("http://127.0.0.1:7749/callback"), Is.Null,
            "a desktop app that listens on loopback is RFC 8252's other answer, and it needs http");

    [Test]
    public async Task A_malformed_redirect_is_refused_before_anything_reads_its_scheme()
    {
        Assert.Multiple(async () =>
        {
            Assert.That(await Validate(""), Is.Not.Null);
            Assert.That(await Validate("not-a-uri"), Is.Not.Null);
        });
    }
}
