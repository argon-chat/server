namespace ArgonSharedLogicTest;

using Argon.Grains.Interfaces;

/// <summary>
/// Whether a checked address may be mailed, from the facts the check found.
/// </summary>
/// <remarks>
/// The rule is RFC 5321's: mail goes to the MX hosts, and only a domain with no MX falls back to its
/// A/AAAA records. <c>CanSendEmail</c> used to require an A/AAAA record whatever the MX said, so a
/// domain that routes its mail and serves nothing else — a common shape for a mail-only domain — was
/// refused as "does not resolve" by both <c>EmailSendController</c> and every send the platform makes.
/// </remarks>
[TestFixture]
public class EmailValidationResultTests
{
    private static EmailValidationResult Checked(
        bool resolves, bool mx, SmtpCheckStatus smtp = SmtpCheckStatus.NotPerformed, bool syntax = true)
        => new(syntax, "someone@domain.test", "domain.test", resolves, mx, smtp, null);

    [Test]
    public void A_domain_with_mx_records_and_no_address_can_be_sent_to()
    {
        var result = Checked(resolves: false, mx: true);

        Assert.Multiple(() =>
        {
            Assert.That(result.CanSendEmail, Is.True);
            Assert.That(result.FailureReason, Is.Null);
        });
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    public void A_domain_with_somewhere_to_deliver_can_be_sent_to(bool resolves, bool mx)
        => Assert.That(Checked(resolves, mx).CanSendEmail, Is.True);

    [Test]
    public void A_domain_with_neither_mx_nor_address_is_refused_and_says_so()
    {
        var result = Checked(resolves: false, mx: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.CanSendEmail, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo("Domain has no MX and no valid fallback (A/AAAA)."));
        });
    }

    [Test]
    public void An_address_with_bad_syntax_is_refused_before_anything_else_is_considered()
    {
        var result = Checked(resolves: true, mx: true, syntax: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.CanSendEmail, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo("Invalid email syntax."));
        });
    }

    [TestCase(SmtpCheckStatus.Accepted, true, null)]
    [TestCase(SmtpCheckStatus.Rejected, false, "SMTP server rejected recipient (550/551/553).")]
    [TestCase(SmtpCheckStatus.TemporaryFailure, false, "SMTP temporary failure (4xx).")]
    [TestCase(SmtpCheckStatus.CouldNotConnect, false, "Could not connect to any SMTP server.")]
    [TestCase(SmtpCheckStatus.Inconclusive, false, "SMTP check was inconclusive.")]
    public void An_smtp_verdict_decides_a_domain_that_can_otherwise_receive(SmtpCheckStatus smtp, bool sendable, string? reason)
    {
        var result = Checked(resolves: true, mx: true, smtp);

        Assert.Multiple(() =>
        {
            Assert.That(result.CanSendEmail, Is.EqualTo(sendable));
            Assert.That(result.FailureReason, Is.EqualTo(reason));
        });
    }
}
