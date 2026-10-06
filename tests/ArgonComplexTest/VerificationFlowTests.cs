namespace ArgonComplexTest.Tests;

using Argon.Features.Testing;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using ion.runtime.client;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

/// <summary>
/// The step-up verification flow in front of sensitive actions: what it asks for, how wrong answers
/// are counted, and what it is bound to. <see cref="AccountSecurityTests"/> covers the email change
/// that sits behind it.
/// </summary>
[TestFixture]
public class VerificationFlowTests : TestBase
{
    private RecordingEmailSink Mail => FactoryAsp.Services.GetRequiredService<RecordingEmailSink>();

    private static async Task<VerificationFlow> BeginAsync(TestUserSession account, CancellationToken ct)
    {
        var begun = await account.Security.BeginVerification(SensitiveAction.CHANGE_EMAIL, ct);

        Assert.That(begun, Is.InstanceOf<SuccessBeginVerification>(), $"{(begun as FailedBeginVerification)?.error}");

        return ((SuccessBeginVerification)begun).flow;
    }

    [Test, CancelAfter(120_000)]
    public async Task Changing_the_email_asks_for_the_password_and_a_code_from_the_current_address(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var email   = account.Credentials.email;
        var flow    = await BeginAsync(account, ct);

        var password = await account.Security.SubmitVerification(flow.flowId, VerificationFactor.PASSWORD, account.Credentials.password, ct);
        var sent     = await account.Security.ChallengeVerification(flow.flowId, VerificationFactor.EMAIL_CODE, ct);
        var mail     = await Mail.WaitForAsync(email, EmailKinds.VerificationCode, TimeSpan.FromSeconds(5), ct);
        var code     = await NextEmailCodeAsync(email, null, ct);
        var verified = await account.Security.SubmitVerification(flow.flowId, VerificationFactor.EMAIL_CODE, code, ct);

        Assert.Multiple(() =>
        {
            Assert.That(flow.requirements.Select(r => r.factors.ToArray()),
                Is.EqualTo(new[] { new[] { VerificationFactor.PASSWORD }, new[] { VerificationFactor.EMAIL_CODE } }));
            Assert.That(flow.verified, Is.False);
            Assert.That(flow.attemptsLeft, Is.EqualTo(5));

            Assert.That((password as SuccessSubmitVerification)?.flow.verified, Is.False,
                "the password alone verified the flow");
            Assert.That((sent as VerificationCodeSent)?.destination, Is.EqualTo($"{email[0]}***{email[email.IndexOf('@')..]}"));
            Assert.That(mail?.Body, Does.Contain("change the email address"), "the code mail does not say what it unlocks");
            Assert.That((verified as SuccessSubmitVerification)?.flow.verified, Is.True);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Wrong_answers_share_one_budget_and_spending_it_ends_the_flow(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var flow    = await BeginAsync(account, ct);

        var answers = new List<ISubmitVerificationResult>();

        for (var i = 0; i < 5; i++)
            answers.Add(await account.Security.SubmitVerification(flow.flowId, VerificationFactor.PASSWORD, $"wrong-{i}", ct));

        var right = await account.Security.SubmitVerification(flow.flowId, VerificationFactor.PASSWORD, account.Credentials.password, ct);

        Assert.Multiple(() =>
        {
            Assert.That(answers.Take(4).Select(a => (a as FailedSubmitVerification)?.error), Is.All.EqualTo(VerificationError.INVALID_PROOF));
            Assert.That(answers.Select(a => (a as FailedSubmitVerification)?.attemptsLeft), Is.EqualTo(new int?[] { 4, 3, 2, 1, 0 }));
            Assert.That((answers[4] as FailedSubmitVerification)?.error, Is.EqualTo(VerificationError.TOO_MANY_ATTEMPTS));
            Assert.That((right as FailedSubmitVerification)?.error, Is.EqualTo(VerificationError.FLOW_EXPIRED),
                "the right password was still accepted after the budget was spent");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_code_is_only_checked_once_it_was_sent_and_resent_after_the_cooldown(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var flow    = await BeginAsync(account, ct);

        var early   = await account.Security.SubmitVerification(flow.flowId, VerificationFactor.EMAIL_CODE, "123456", ct);
        var first   = await account.Security.ChallengeVerification(flow.flowId, VerificationFactor.EMAIL_CODE, ct);
        var resend  = await account.Security.ChallengeVerification(flow.flowId, VerificationFactor.EMAIL_CODE, ct);

        Assert.Multiple(() =>
        {
            Assert.That((early as FailedSubmitVerification)?.error, Is.EqualTo(VerificationError.CHALLENGE_REQUIRED));
            Assert.That((early as FailedSubmitVerification)?.attemptsLeft, Is.EqualTo(5), "a code nobody was sent cost an attempt");
            Assert.That(first, Is.InstanceOf<VerificationCodeSent>());
            Assert.That((resend as FailedChallengeVerification)?.error, Is.EqualTo(VerificationError.RATE_LIMITED));
            Assert.That((resend as FailedChallengeVerification)?.resendAt, Is.EqualTo((first as VerificationCodeSent)?.resendAt),
                "the refusal does not say when a resend is allowed");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Factors_outside_the_policy_are_refused(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var flow    = await BeginAsync(account, ct);

        var totp      = await account.Security.SubmitVerification(flow.flowId, VerificationFactor.TOTP, "123456", ct);
        var passkey   = await account.Security.ChallengeVerification(flow.flowId, VerificationFactor.PASSKEY, ct);
        var noSending = await account.Security.ChallengeVerification(flow.flowId, VerificationFactor.PASSWORD, ct);

        await account.Security.SubmitVerification(flow.flowId, VerificationFactor.PASSWORD, account.Credentials.password, ct);
        var twice = await account.Security.SubmitVerification(flow.flowId, VerificationFactor.PASSWORD, account.Credentials.password, ct);

        Assert.Multiple(() =>
        {
            Assert.That((totp as FailedSubmitVerification)?.error, Is.EqualTo(VerificationError.FACTOR_NOT_ALLOWED),
                "an account without an authenticator app was offered one");
            Assert.That((passkey as FailedChallengeVerification)?.error, Is.EqualTo(VerificationError.FACTOR_NOT_ALLOWED));
            Assert.That((noSending as FailedChallengeVerification)?.error, Is.EqualTo(VerificationError.FACTOR_NOT_ALLOWED));
            Assert.That((twice as FailedSubmitVerification)?.error, Is.EqualTo(VerificationError.FACTOR_NOT_ALLOWED),
                "a satisfied requirement was accepted again");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task An_authenticator_app_stands_in_for_the_emailed_code(CancellationToken ct = default)
    {
        var account  = await CreateSessionAsync(ct);
        var enabling = (SuccessEnableOTP)await account.Security.EnableOTP(ct);
        var secret   = Base32Encoding.ToBytes(enabling.secret);

        Assert.That(await account.Security.VerifyAndEnableOTP(new Totp(secret).ComputeTotp(), ct), Is.InstanceOf<SuccessVerifyOTP>());

        var flow = await BeginAsync(account, ct);

        await account.Security.SubmitVerification(flow.flowId, VerificationFactor.PASSWORD, account.Credentials.password, ct);
        var verified = await account.Security.SubmitVerification(flow.flowId, VerificationFactor.TOTP, new Totp(secret).ComputeTotp(), ct);

        Assert.Multiple(() =>
        {
            Assert.That(flow.requirements.Last().factors, Is.EqualTo(new[] { VerificationFactor.EMAIL_CODE, VerificationFactor.TOTP }));
            Assert.That((verified as SuccessSubmitVerification)?.flow.verified, Is.True);
            Assert.That(Mail.Sent(account.Credentials.email, EmailKinds.VerificationCode), Is.Empty,
                "a code was mailed though the authenticator app was used");
        });
    }

    /// <summary>
    /// A flow belongs to the session that began it: another signed-in device of the same account can
    /// neither answer it nor spend it, even holding its id.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_flow_is_bound_to_the_session_that_began_it(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var other   = await SecondDeviceAsync(account, ct);
        var flowId  = await VerifiedFlowAsync(account, SensitiveAction.CHANGE_EMAIL, ct);

        var submitted = await other.Security.SubmitVerification(flowId, VerificationFactor.PASSWORD, account.Credentials.password, ct);
        var requested = await other.Security.RequestEmailChange(flowId, $"moved_{Guid.NewGuid():N}@test.local", ct);
        var own       = await account.Security.RequestEmailChange(flowId, $"moved_{Guid.NewGuid():N}@test.local", ct);

        Assert.Multiple(() =>
        {
            Assert.That((submitted as FailedSubmitVerification)?.error, Is.EqualTo(VerificationError.FLOW_EXPIRED));
            Assert.That((requested as FailedRequestEmailChange)?.error, Is.EqualTo(EmailChangeError.VERIFICATION_REQUIRED),
                "another device used a flow it did not verify");
            Assert.That(own, Is.InstanceOf<SuccessRequestEmailChange>());
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_new_flow_replaces_the_last_and_a_cancelled_one_is_gone(CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);
        var first   = await BeginAsync(account, ct);
        var second  = await BeginAsync(account, ct);

        var replaced = await account.Security.SubmitVerification(first.flowId, VerificationFactor.PASSWORD, account.Credentials.password, ct);

        await account.Security.CancelVerification(second.flowId, ct);
        var cancelled = await account.Security.SubmitVerification(second.flowId, VerificationFactor.PASSWORD, account.Credentials.password, ct);

        Assert.Multiple(() =>
        {
            Assert.That((replaced as FailedSubmitVerification)?.error, Is.EqualTo(VerificationError.FLOW_EXPIRED));
            Assert.That((cancelled as FailedSubmitVerification)?.error, Is.EqualTo(VerificationError.FLOW_EXPIRED));
        });
    }

    private async Task<TestUserSession> SecondDeviceAsync(TestUserSession account, CancellationToken ct)
    {
        var interceptor = new DefaultHeaderInterceptor();
        var client      = IonClient.Create(HttpClient, WsFactory);

        client.WithInterceptor(interceptor);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var result = await client.ForService<IIdentityInteraction>(scope.ServiceProvider).Authorize(
            new UserCredentialsInput(account.Credentials.email, null, null, account.Credentials.password, null, null), ct);

        if (result is not SuccessAuthorize authorized)
        {
            Assert.Fail($"could not sign the account in on a second device: {(result as FailedAuthorize)?.error}");
            return null!;
        }

        interceptor.SetToken(authorized.token);

        return new TestUserSession(client, FactoryAsp.Services, account.Credentials, authorized.token, interceptor.SessionId)
        {
            UserId = account.UserId
        };
    }
}
