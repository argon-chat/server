namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Signing a desktop in by scanning a code on it with an already-signed-in phone.
/// </summary>
/// <remarks>
/// <para>Three parties in every test: the <em>browser</em> — unauthenticated, owns the code and is
/// the only machine allowed to collect the result; the <em>phone</em> — signed in, decides; and the
/// cache record between them, which holds a real JWT for the few seconds between the tap and the
/// next poll.</para>
///
/// <para>Most of what is worth asserting here is not the happy path but the refusals: a code that
/// works twice, a code that pays out to whoever polls first, or a rejection that reads as an expiry
/// are each a way for this feature to be worse than no feature. Each has a test below.</para>
/// </remarks>
[TestFixture]
public class QrLoginTests : TestBase
{
    /// <summary>Asks for a code as the desktop would, and fails the test if the server refuses.</summary>
    private static async Task<LoginRequestTicket> RequestCodeAsync(TestBrowser browser, CancellationToken ct)
    {
        var created = await browser.Identity.CreateLoginRequest(ct);

        if (created is FailedCreateLoginRequest failed)
            Assert.Fail($"Could not create a login request: {failed.error}");

        return ((SuccessCreateLoginRequest)created).ticket;
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task CreateLoginRequest_WithoutAnyToken_ReturnsScannableCode(CancellationToken ct = default)
    {
        var browser = CreateBrowser();

        var ticket = await RequestCodeAsync(browser, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ticket.token, Is.Not.Empty, "the QR has nothing to encode");

            // Hex of 24 random bytes. Asserted because the alphabet is what makes the code
            // scannable in the QR alphanumeric mode and typeable if scanning fails.
            Assert.That(ticket.token, Has.Length.EqualTo(48));
            Assert.That(ticket.token, Does.Match("^[0-9a-f]+$"));

            Assert.That(ticket.expiresAt, Is.GreaterThan(DateTimeOffset.UtcNow));
        });
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task PreviewLoginRequest_DescribesTheBrowserThatAsked(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var browser = CreateBrowser();
        var ticket  = await RequestCodeAsync(browser, ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var preview = await GetIdentityService(scope.ServiceProvider).PreviewLoginRequest(ticket.token, ct);

        if (preview is FailedLoginRequestPreview failed)
            Assert.Fail($"Preview failed: {failed.error}");

        var card = ((SuccessLoginRequestPreview)preview).preview;

        Assert.Multiple(() =>
        {
            // The card exists to answer "was this me?", and it cannot answer that with blanks.
            Assert.That(card.clientName, Is.Not.Empty);
            Assert.That(card.ip, Is.Not.Empty);
            Assert.That(card.createdAt, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
            Assert.That(card.expiresAt, Is.GreaterThan(card.createdAt));
        });
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task PreviewLoginRequest_WithUnknownCode_ReturnsNotFound(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var preview = await GetIdentityService(scope.ServiceProvider)
            .PreviewLoginRequest(new string('0', 48), ct);

        Assert.That(preview, Is.InstanceOf<FailedLoginRequestPreview>());
        Assert.That(((FailedLoginRequestPreview)preview).error, Is.EqualTo(LoginRequestError.NOT_FOUND));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task ApprovedRequest_HandsATokenToTheBrowserThatAsked(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var browser = CreateBrowser();
        var ticket  = await RequestCodeAsync(browser, ct);

        // Before the tap the desktop is told to keep waiting, not that something went wrong.
        var pending = await browser.Identity.PollLoginRequest(ticket.token, ct);
        Assert.That(pending, Is.InstanceOf<PendingLoginRequest>());

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var approved = await GetIdentityService(scope.ServiceProvider).ApproveLoginRequest(ticket.token, ct);
        Assert.That(approved, Is.InstanceOf<SuccessApproveLoginRequest>());

        var collected = await browser.Identity.PollLoginRequest(ticket.token, ct);

        Assert.That(collected, Is.InstanceOf<ApprovedLoginRequest>());
        Assert.That(((ApprovedLoginRequest)collected).token, Is.Not.Empty);
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task ApprovedRequest_IsBurntOnCollection(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var browser = CreateBrowser();
        var ticket  = await RequestCodeAsync(browser, ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        await GetIdentityService(scope.ServiceProvider).ApproveLoginRequest(ticket.token, ct);

        var first  = await browser.Identity.PollLoginRequest(ticket.token, ct);
        var second = await browser.Identity.PollLoginRequest(ticket.token, ct);

        Assert.That(first, Is.InstanceOf<ApprovedLoginRequest>());

        // A code that pays out twice is a code that can be replayed from a captured response.
        Assert.That(second, Is.InstanceOf<FailedLoginPoll>());
        Assert.That(((FailedLoginPoll)second).error, Is.EqualTo(LoginRequestError.NOT_FOUND));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task PollLoginRequest_FromAnotherMachine_IsRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var browser   = CreateBrowser();
        var bystander = CreateBrowser();

        var ticket = await RequestCodeAsync(browser, ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        await GetIdentityService(scope.ServiceProvider).ApproveLoginRequest(ticket.token, ct);

        // Someone who photographed the screen polls first. Without the machine check they would
        // walk away with a working session, and the approval would look ordinary to its owner.
        var stolen = await bystander.Identity.PollLoginRequest(ticket.token, ct);

        Assert.That(stolen, Is.InstanceOf<FailedLoginPoll>());
        Assert.That(((FailedLoginPoll)stolen).error, Is.EqualTo(LoginRequestError.DEVICE_MISMATCH));

        // And the refusal must not have consumed the record: the real desktop is still waiting.
        var collected = await browser.Identity.PollLoginRequest(ticket.token, ct);
        Assert.That(collected, Is.InstanceOf<ApprovedLoginRequest>());
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task ApproveLoginRequest_Twice_ReportsTheCodeAsSpent(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var browser = CreateBrowser();
        var ticket  = await RequestCodeAsync(browser, ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var identity = GetIdentityService(scope.ServiceProvider);

        var first  = await identity.ApproveLoginRequest(ticket.token, ct);
        var second = await identity.ApproveLoginRequest(ticket.token, ct);

        Assert.That(first, Is.InstanceOf<SuccessApproveLoginRequest>());

        Assert.That(second, Is.InstanceOf<FailedApproveLoginRequest>());
        Assert.That(((FailedApproveLoginRequest)second).error, Is.EqualTo(LoginRequestError.ALREADY_USED));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task PreviewLoginRequest_AfterApproval_ReportsTheCodeAsSpent(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var browser = CreateBrowser();
        var ticket  = await RequestCodeAsync(browser, ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var identity = GetIdentityService(scope.ServiceProvider);
        await identity.ApproveLoginRequest(ticket.token, ct);

        // A second phone scanning the same screen should not be offered the card again.
        var preview = await identity.PreviewLoginRequest(ticket.token, ct);

        Assert.That(preview, Is.InstanceOf<FailedLoginRequestPreview>());
        Assert.That(((FailedLoginRequestPreview)preview).error, Is.EqualTo(LoginRequestError.ALREADY_USED));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task RejectedRequest_TellsTheDesktopItWasRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var browser = CreateBrowser();
        var ticket  = await RequestCodeAsync(browser, ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var rejected = await GetIdentityService(scope.ServiceProvider).RejectLoginRequest(ticket.token, ct);
        Assert.That(rejected, Is.InstanceOf<SuccessRejectLoginRequest>());

        var polled = await browser.Identity.PollLoginRequest(ticket.token, ct);

        // Specifically a rejection and not NOT_FOUND: "your code expired" invites another try,
        // which is the opposite of what someone who just pressed «Это не я» asked for.
        Assert.That(polled, Is.InstanceOf<RejectedLoginRequest>());
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task ApproveLoginRequest_WithoutSigningIn_IsRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var browser = CreateBrowser();
        var ticket  = await RequestCodeAsync(browser, ct);

        // The browser holding the code must not be able to approve its own request — that would
        // turn the whole flow into "anyone can mint a session".
        Assert.That(
            async () => await browser.Identity.ApproveLoginRequest(ticket.token, ct),
            Throws.Exception,
            "approving is not anonymous");
    }
}
