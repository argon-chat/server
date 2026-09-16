namespace ArgonSharedLogicTest;

using System.IdentityModel.Tokens.Jwt;
using Argon.Features.Jwt;
using Argon.Features.WebSession;
using Microsoft.Extensions.Options;

/// <summary>
/// That a browser's access token is the short-lived one and an installed client's is not.
/// </summary>
/// <remarks>
/// <para>The asymmetry is the whole design and it is invisible from the outside: both tokens are
/// signed the same way by the same code, and the only thing separating them is an argument that
/// defaults to null. Deleted, or dropped while threading a new overload through, nothing fails —
/// browsers would quietly go back to carrying a credential good for days, which is exactly the
/// state this replaced.</para>
///
/// <para>The keys are the committed development pair from <c>deploy/dev/conf.d/jwt.json</c>. They
/// are in the repository on purpose so a checkout runs without a setup step, and signing with them
/// here proves the real path rather than a stub of it.</para>
/// </remarks>
[TestFixture]
public class WebAccessTokenLifetimeTests
{
    private const string PrivateKey =
        "MHcCAQEEIOHrEfaTwya3mBYrciom96rdZwFwiQFTLkqdKk5FwLAzoAoGCCqGSM49AwEHoUQDQgAEmRx+QDVOdY9piZYVgVkR1k0QSdS9"
      + "65dLHfrRwPnCh26GRb5ZXxijLCleTs174CjYvH1KzYEE/AaG1FhViwkCLg==";

    private const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEmRx+QDVOdY9piZYVgVkR1k0QSdS965dLHfrRwPnCh26GRb5ZXxijLCleTs174CjYvH1K"
      + "zYEE/AaG1FhViwkCLg==";

    private static readonly TimeSpan Configured = TimeSpan.FromDays(7);

    private static ClassicJwtFlow Flow()
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer              = "Argon",
            Audience            = "Argon",
            MachineSalt         = "5369f453f6272967c87fedaf258ee71de1f7fcf642f76fdad23bd761f409a4cf",
            AccessTokenLifetime = Configured,
            CertificateBase64   = new KeyPair(PrivateKey, PublicKey, "")
        });

        return new ClassicJwtFlow(options, new WrapperForSignKey(options));
    }

    private static TimeSpan LifetimeOf(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        return jwt.ValidTo - jwt.ValidFrom;
    }

    [Test]
    public void A_token_with_no_lifetime_asked_for_gets_the_deployments_own()
    {
        var token = Flow().GenerateAccessToken(Guid.NewGuid(), "a-machine", ["argon.app"]);

        Assert.That(LifetimeOf(token), Is.EqualTo(Configured).Within(TimeSpan.FromSeconds(2)),
            "an installed client's token stopped using Jwt:AccessTokenLifetime");
    }

    [Test]
    public void A_browsers_token_is_cut_to_the_web_sessions_lifetime()
    {
        var web = new WebSessionOptions().AccessTokenLifetime;

        var token = Flow().GenerateAccessToken(Guid.NewGuid(), "a-machine", ["argon.app"], null, web);

        Assert.Multiple(() =>
        {
            Assert.That(LifetimeOf(token), Is.EqualTo(web).Within(TimeSpan.FromSeconds(2)),
                "the lifetime asked for was not the lifetime written onto the token");
            Assert.That(web, Is.LessThan(TimeSpan.FromHours(1)),
                "the default web session lifetime is no longer short, which is the only reason it exists");
        });
    }

    /// <summary>
    /// The overload without a machine id takes it too — that is the one the console paths mint with,
    /// and a lifetime that applied to only one of the two would be a gap nobody would notice.
    /// </summary>
    [Test]
    public void The_machineless_overload_honours_it_as_well()
    {
        var token = Flow().GenerateAccessToken(Guid.NewGuid(), ["argon.app"], null, TimeSpan.FromMinutes(5));

        Assert.That(LifetimeOf(token), Is.EqualTo(TimeSpan.FromMinutes(5)).Within(TimeSpan.FromSeconds(2)));
    }
}
