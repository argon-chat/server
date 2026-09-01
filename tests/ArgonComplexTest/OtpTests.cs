namespace ArgonComplexTest;

using System.Net.WebSockets;
using Argon.Features.Clustering;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using ion.runtime.client;
using Microsoft.AspNetCore.Mvc.Testing;
using OtpNet;

/// <summary>
/// Turning two-factor authentication on and off, over a connection that actually leaves the process.
/// </summary>
/// <remarks>
/// <para><b>Both halves of this fixture exist because the old OTP tests had neither.</b></para>
///
/// <para>The first is the cluster. Those tests ran against the co-hosted host, where a grain call
/// never leaves the process and its result is handed over as an object. Production splits the client
/// role from the silo, so the same call is serialized — and <c>ISecurityGrain</c> returns Ion unions,
/// which Orleans could not put on the wire. Every one of its seventeen methods answered <c>500</c> in
/// production while the suite stayed green. So this fixture drives a client role of its own, joined
/// to the same cluster: the request crosses a real Orleans connection and the response is really
/// encoded.</para>
///
/// <para>The second is the flow. The old tests enabled OTP and then only ever sent
/// <c>"000000"</c> — nothing ever computed a real code, so nothing ever reached the state where OTP
/// is <i>on</i>, and neither verification nor disabling was covered at all. Here the code is derived
/// from the secret the server hands back, which is the only way to exercise what a user does.</para>
///
/// <para>Whether OTP ended up on is asked through the same surface rather than through a getter: a
/// second <c>EnableOTP</c> is refused once it is enabled and accepted once it is not, so the answer
/// comes from the behaviour under test instead of from a field that could agree with itself.</para>
/// </remarks>
[TestFixture]
public class OtpTests : TestBase
{
    private RoleHost entry = null!;

    [OneTimeSetUp]
    public void StartAClientRole()
        => entry = new RoleHost(ArgonTestEnvironment.Instance.Host.Settings, ArgonRoleId.EntryPoint,
            siloPort: 0, ArgonClusterEndpoints.DefaultClusterId);

    [OneTimeTearDown]
    public async Task StopTheClientRole()
        => await entry.DisposeAsync();

    /// <summary>
    /// The call the outage was reported on, made the way production makes it.
    /// </summary>
    /// <remarks>
    /// Asserting only that a secret comes back, because that is all the bug destroyed: the grain did
    /// its work and the answer could not be encoded, so the caller got <c>500</c> and no explanation.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task Enabling_otp_from_a_client_role_returns_a_secret(CancellationToken ct = default)
    {
        var security = await SignedInSecurityService(ct);

        var result = await security.EnableOTP(ct);

        Assert.That(result, Is.InstanceOf<SuccessEnableOTP>(),
            "the union came back over a real Orleans connection — if this throws instead, the result "
          + "type cannot be serialized and every security call is a 500 in production");

        var success = (SuccessEnableOTP)result;

        Assert.Multiple(() =>
        {
            Assert.That(success.secret, Is.Not.Empty);
            Assert.That(success.qrCodeUrl, Does.Contain("otpauth://totp/"));
        });
    }

    /// <summary>
    /// The whole round: on, proven on, then off, proven off.
    /// </summary>
    /// <remarks>
    /// One test rather than four because the states only exist in sequence — there is no way to be
    /// "enabled" without having verified, and asserting each step separately would mean repeating the
    /// steps before it and calling that coverage.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task A_code_derived_from_the_secret_turns_otp_on_and_another_turns_it_off(
        CancellationToken ct = default)
    {
        var security = await SignedInSecurityService(ct);

        var enabling = (SuccessEnableOTP)await security.EnableOTP(ct);

        var verified = await security.VerifyAndEnableOTP(CodeFor(enabling.secret), ct);
        Assert.That(verified, Is.InstanceOf<SuccessVerifyOTP>(), "a code computed from the server's own secret was refused");

        // Proof that it is on: enabling again is the one thing an enabled account refuses.
        var again = await security.EnableOTP(ct);
        Assert.That(again, Is.InstanceOf<FailedEnableOTP>());
        Assert.That(((FailedEnableOTP)again).error, Is.EqualTo(OTPError.ALREADY_ENABLED),
            "verification reported success without actually storing the secret");

        var disabled = await security.DisableOTP(CodeFor(enabling.secret), ct);
        Assert.That(disabled, Is.InstanceOf<SuccessDisableOTP>());

        // And proof that it is off: the same call is accepted again.
        Assert.That(await security.EnableOTP(ct), Is.InstanceOf<SuccessEnableOTP>(),
            "disabling reported success but left the secret in place");
    }

    /// <summary>
    /// A wrong code neither enables nor disables.
    /// </summary>
    /// <remarks>
    /// The disabling half is the one worth having: a bug that accepted any code there would let
    /// anyone holding a stolen session strip the second factor off the account, and it is invisible
    /// from the enabling side, which never had a stored secret to protect.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task A_wrong_code_changes_nothing(CancellationToken ct = default)
    {
        var security = await SignedInSecurityService(ct);

        var enabling = (SuccessEnableOTP)await security.EnableOTP(ct);

        var refusedVerify = await security.VerifyAndEnableOTP(WrongCodeFor(enabling.secret), ct);
        Assert.That(refusedVerify, Is.InstanceOf<FailedVerifyOTP>());
        Assert.That(((FailedVerifyOTP)refusedVerify).error, Is.EqualTo(OTPError.INVALID_CODE));

        // Now really enable it, so the refusal below is about the code and not about there being
        // nothing to disable.
        Assert.That(await security.VerifyAndEnableOTP(CodeFor(enabling.secret), ct),
            Is.InstanceOf<SuccessVerifyOTP>());

        var refusedDisable = await security.DisableOTP(WrongCodeFor(enabling.secret), ct);
        Assert.That(refusedDisable, Is.InstanceOf<FailedDisableOTP>());
        Assert.That(((FailedDisableOTP)refusedDisable).error, Is.EqualTo(OTPError.INVALID_CODE));

        // Still on, so the wrong code did not quietly remove it.
        Assert.That(await security.EnableOTP(ct), Is.InstanceOf<FailedEnableOTP>());
    }

    /// <summary>
    /// Nothing to verify and nothing to disable, before OTP is asked for at all.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public async Task An_account_without_otp_has_nothing_to_verify_or_disable(CancellationToken ct = default)
    {
        var security = await SignedInSecurityService(ct);

        var verify  = await security.VerifyAndEnableOTP("000000", ct);
        var disable = await security.DisableOTP("000000", ct);

        Assert.Multiple(() =>
        {
            Assert.That(((FailedVerifyOTP)verify).error, Is.EqualTo(OTPError.NOT_ENABLED));
            Assert.That(((FailedDisableOTP)disable).error, Is.EqualTo(OTPError.NOT_ENABLED));
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The code an authenticator app would be showing right now.</summary>
    private static string CodeFor(string base32Secret)
        => new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp();

    /// <summary>
    /// A code of the right shape that this secret will not produce.
    /// </summary>
    /// <remarks>
    /// Derived from the real one rather than hard-coded: <c>"000000"</c> is a code some secret
    /// legitimately produces at some moment, and a test that happened to run at that moment would
    /// fail for a reason nobody could reproduce.
    /// </remarks>
    private static string WrongCodeFor(string base32Secret)
    {
        var real = CodeFor(base32Secret);

        return string.Concat(real.Select(digit => digit == '0' ? '1' : '0'));
    }

    /// <summary>
    /// A freshly registered account, reached through the client role rather than the co-hosted host.
    /// </summary>
    /// <remarks>
    /// Registered through the same client it will be used from: the tokens this server mints carry a
    /// hash of the caller's machine id, so an account created through one client and used from
    /// another is refused for that reason rather than for anything a test meant to assert.
    /// </remarks>
    private async Task<ISecurityInteraction> SignedInSecurityService(CancellationToken ct)
    {
        var http = entry.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress       = new Uri("https://entry.test.local"),
            AllowAutoRedirect = false
        });

        var interceptor = new DefaultHeaderInterceptor();
        var client      = IonClient.Create(http, NoWebSockets);

        client.WithInterceptor(interceptor);

        var credentials = GenerateCredentials();

        var registration = await client.ForService<IIdentityInteraction>(entry.Services).Registration(
            new NewUserCredentialsInput(
                credentials.email, credentials.username, credentials.password, credentials.displayName,
                credentials.argreeTos, credentials.birthDate, credentials.argreeOptionalEmails,
                credentials.captchaToken, "1.0", "1.0"),
            ct);

        if (registration is not SuccessRegistration success)
        {
            Assert.Fail($"could not register through the client role: {(registration as FailedRegistration)?.error}");
            return null!;
        }

        interceptor.SetToken(success.token);

        return client.ForService<ISecurityInteraction>(entry.Services);
    }

    private static Task<WebSocket> NoWebSockets(Uri uri, CancellationToken ct, string[]? protocols)
        => throw new NotSupportedException("this fixture only makes unary calls");
}
