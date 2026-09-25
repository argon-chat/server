namespace ArgonComplexTest;

using System.Net;
using System.Net.Sockets;
using Argon.Features.Email;
using Argon.Features.Template;
using Argon.Features.Testing;
using Argon.Grains;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// The mail grain itself: every template it can send, the address check in front of SMTP, and what
/// the journal says happened.
/// </summary>
/// <remarks>
/// <para>The account fixtures observe mail through <see cref="RecordingEmailSink"/>, which is enough to
/// say a message was decided on. It says nothing about the half of <c>EmailManager</c> that runs once
/// SMTP is switched on — rendering the form, the MX/A check, the connection, and the journal line that
/// is the only record of whether a message left the building — because the integration host runs
/// with SMTP off, as every host without a mail server does.</para>
///
/// <para>So there are two instances here. The hosted grain, through the grain factory, for everything
/// the suite's configuration can show; and a second <see cref="EmailManager"/> built from the host's
/// own services with one difference, an <see cref="SmtpConfig"/> that is switched on and points at a
/// loopback listener that hangs up or never answers. Nothing about the send path is stubbed: the
/// templates, the journal and the sink are the host's, the DNS lookups are real, and the connection is
/// a real TLS attempt against a server that refuses it — which is the one outcome a test can produce
/// without a trusted certificate, and the one the journal exists to record.</para>
///
/// <para>Addresses that must pass the MX/A check use <see cref="DeliverableDomain"/>, a real mailbox
/// provider, and the tests that need it stand down when this host has no resolver. No mail is ever
/// delivered: the SMTP host is the loopback listener whatever the recipient's domain is.</para>
/// </remarks>
[TestFixture]
public class EmailManagerTests : TestBase
{
    /// <summary>A domain with both MX and A records, for an address the check lets through.</summary>
    private const string DeliverableDomain = "gmail.com";

    /// <summary>A domain with A records and no MX at all — delivery by the RFC 5321 fallback.</summary>
    private const string FallbackOnlyDomain = "one.one.one.one";

    /// <summary>A domain whose only MX is <c>.</c> — RFC 7505's "this domain accepts no mail".</summary>
    private const string NullMxDomain = "example.com";

    private const string AppName = "Coverage App";

    private IEmailManager HostedMail => GetGrainFactory().GetGrain<IEmailManager>(Guid.Empty);

    private IEmailJournal Journal => FactoryAsp.Services.GetRequiredService<IEmailJournal>();

    // ── The hosted grain, SMTP off ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A magic link carries the link, the application and how long it lasts, and the journal says it
    /// did not go out.
    /// </summary>
    /// <remarks>
    /// Nothing in the product sends one yet, which is exactly why it is asserted here: a template
    /// nobody renders is one that can drift out of step with its call site the day somebody wires it.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_magic_link_carries_the_link_the_app_and_its_lifetime(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var to      = session.Credentials.email;
        var link    = $"https://app.test.local/magic/{Guid.NewGuid():N}";

        await HostedMail.SendMagicLinkAsync(to, link, AppName, TimeSpan.FromMinutes(15));

        var mail  = await AccountTimings.Emails.WaitForAsync(to, EmailKinds.MagicLink, AccountTimings.Slack, ct);
        var entry = await JournaledAsync(session.UserId, EmailKinds.MagicLink, ct);

        Assert.That(mail, Is.Not.Null, "the magic link was never handed to the mail grain's observer");

        Assert.Multiple(() =>
        {
            Assert.That(mail!.Subject, Is.EqualTo($"Sign in to {AppName}"));
            Assert.That(mail.Body, Does.Contain(link));
            Assert.That(mail.Body, Does.Contain(AppName));
            Assert.That(mail.Body, Does.Contain("15"), "the validity is rendered in whole minutes");

            Assert.That(entry, Is.Not.Null, "a magic link left no line in the account's journal");
            Assert.That(entry?.Delivered, Is.False);
            Assert.That(entry?.Error, Is.EqualTo("smtp disabled"));
        });
    }

    /// <summary>
    /// An invitation carries the link and its lifetime in hours, and is journaled without an account.
    /// </summary>
    /// <remarks>
    /// <para>The address belongs to nobody yet — that is what an invitation is — so the journal records
    /// it with no account id rather than with the address, as <see cref="IEmailJournal"/> promises.
    /// Read off the platform-wide page and matched on kind and time, which is safe because nothing
    /// else in the suite sends an invitation.</para>
    ///
    /// <para>The subject is asserted exactly because it used to differ between the two halves of the
    /// method: the observer was told "You have been invited to …" while the message built for SMTP
    /// said "You've been invited to …", so the sink's promise — the subject as it would have been
    /// sent — was not kept for this one kind.</para>
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_registration_invite_carries_the_link_and_its_lifetime_in_hours(CancellationToken ct = default)
    {
        var to    = $"invitee_{Guid.NewGuid():N}@test.local";
        var link  = $"https://app.test.local/invite/{Guid.NewGuid():N}";
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        await HostedMail.SendRegistrationInviteAsync(to, link, AppName, TimeSpan.FromHours(48));

        var mail  = await AccountTimings.Emails.WaitForAsync(to, EmailKinds.RegistrationInvite, AccountTimings.Slack, ct);
        var entry = await JournaledAsync(null, EmailKinds.RegistrationInvite, ct, since);

        Assert.That(mail, Is.Not.Null, "the invitation was never handed to the mail grain's observer");

        Assert.Multiple(() =>
        {
            Assert.That(mail!.Subject, Is.EqualTo($"You've been invited to {AppName}"));
            Assert.That(mail.Body, Does.Contain(link));
            Assert.That(mail.Body, Does.Contain("48"), "the validity is rendered in whole hours");

            Assert.That(entry, Is.Not.Null, "the invitation left no line in the platform journal");
            Assert.That(entry?.UserId, Is.Null, "an invitation is journaled without an account");
            Assert.That(entry?.Delivered, Is.False);
        });
    }

    /// <summary>
    /// A raw send with SMTP off answers with a local message id and is journaled like every other kind.
    /// </summary>
    /// <remarks>
    /// The one send that returns something, because <c>EmailSendController</c> hands the id back to the
    /// application that asked. It used to be the one kind the disabled branch did not journal, so a host
    /// without SMTP had a record of every message but the ones sent on an application's behalf.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_raw_send_with_smtp_off_returns_a_local_id_and_is_journaled(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var to      = session.Credentials.email;

        var messageId = await HostedMail.SendRawAsync(to, "Raw coverage", "<p>raw body</p>", null, null);

        var mail  = AccountTimings.Emails.Sent(to, EmailKinds.Raw);
        var entry = await JournaledAsync(session.UserId, EmailKinds.Raw, ct);

        Assert.Multiple(() =>
        {
            Assert.That(messageId, Does.EndWith("@argon.gl"));
            Assert.That(mail, Has.Count.EqualTo(1));
            Assert.That(mail.FirstOrDefault()?.Body, Is.EqualTo("<p>raw body</p>"), "a raw body is sent as given");

            Assert.That(entry, Is.Not.Null, "a raw send with SMTP off left no line in the journal");
            Assert.That(entry?.Delivered, Is.False);
            Assert.That(entry?.Error, Is.EqualTo("smtp disabled"));
        });
    }

    // ── The address check ────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(60_000)]
    public async Task Something_that_is_not_an_address_is_refused_as_a_syntax_error(CancellationToken ct = default)
    {
        var result = await HostedMail.ValidateEMailDestination("not an address");

        Assert.Multiple(() =>
        {
            Assert.That(result.SyntaxValid, Is.False);
            Assert.That(result.CanSendEmail, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo("Invalid email syntax."));
            Assert.That(result.Diagnostic, Does.StartWith("Syntax error"));
        });
    }

    /// <summary>
    /// A domain IDNA cannot encode is refused before anything is looked up.
    /// </summary>
    /// <remarks>
    /// A label longer than 63 octets is legal to the address parser and illegal to IDNA; a domain made
    /// of an ideographic full stop alone parses as a domain and normalises to nothing at all.
    /// </remarks>
    [TestCase("someone@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.com")]
    [TestCase("someone@。")]
    [CancelAfter(60_000)]
    public async Task A_domain_idna_cannot_encode_is_refused_before_any_lookup(string address, CancellationToken ct = default)
    {
        var result = await HostedMail.ValidateEMailDestination(address);

        Assert.Multiple(() =>
        {
            Assert.That(result.CanSendEmail, Is.False);
            Assert.That(result.NormalizedAddress, Is.Null);
            Assert.That(result.Diagnostic, Does.StartWith("The domain does not match the IDNA"));
        });
    }

    /// <summary>
    /// An internationalised domain is looked up — and reported — in its ASCII form, whichever dot it
    /// was typed with.
    /// </summary>
    /// <remarks>
    /// Under <c>.invalid</c>, which RFC 2606 reserves so that it can never resolve: the normalisation is
    /// the subject, and the answer to the lookup is fixed whatever resolver this host has.
    /// </remarks>
    [TestCase("user@bücher.invalid")]
    [TestCase("user@bücher．invalid")]
    [CancelAfter(60_000)]
    public async Task An_internationalised_domain_is_normalised_to_punycode(string address, CancellationToken ct = default)
    {
        var result = await HostedMail.ValidateEMailDestination(address);

        Assert.Multiple(() =>
        {
            Assert.That(result.SyntaxValid, Is.True);
            Assert.That(result.DomainPunycode, Is.EqualTo("xn--bcher-kva.invalid"));
            Assert.That(result.NormalizedAddress, Is.EqualTo("user@xn--bcher-kva.invalid"));
            Assert.That(result.CanSendEmail, Is.False, "a reserved domain has nothing to deliver to");
        });
    }

    [Test, CancelAfter(60_000)]
    public async Task A_domain_that_does_not_exist_is_refused(CancellationToken ct = default)
    {
        var result = await HostedMail.ValidateEMailDestination("someone@argon-coverage.invalid");

        Assert.Multiple(() =>
        {
            Assert.That(result.SyntaxValid, Is.True);
            Assert.That(result.DomainResolves, Is.False);
            Assert.That(result.MxRecordsPresent, Is.False);
            Assert.That(result.CanSendEmail, Is.False);
            Assert.That(result.FailureReason, Is.EqualTo("Domain has no MX and no valid fallback (A/AAAA)."));
            Assert.That(result.Diagnostic, Is.EqualTo("The domain does not have an MX and it does not resolve to A/AAAA"));
        });
    }

    [Test, CancelAfter(60_000)]
    public async Task A_domain_with_mail_servers_can_be_sent_to(CancellationToken ct = default)
    {
        await AssumeDnsAsync(DeliverableDomain, ct);

        var result = await HostedMail.ValidateEMailDestination($"someone@{DeliverableDomain}");

        Assert.Multiple(() =>
        {
            Assert.That(result.MxRecordsPresent, Is.True);
            Assert.That(result.CanSendEmail, Is.True, result.Diagnostic);
            Assert.That(result.FailureReason, Is.Null);
            Assert.That(result.Diagnostic, Is.EqualTo("MX is present"));
        });
    }

    [Test, CancelAfter(60_000)]
    public async Task A_domain_with_no_mx_but_an_address_can_be_sent_to_by_the_fallback(CancellationToken ct = default)
    {
        await AssumeDnsAsync(FallbackOnlyDomain, ct);

        var result = await HostedMail.ValidateEMailDestination($"someone@{FallbackOnlyDomain}");

        Assert.Multiple(() =>
        {
            Assert.That(result.MxRecordsPresent, Is.False);
            Assert.That(result.DomainResolves, Is.True);
            Assert.That(result.CanSendEmail, Is.True, result.Diagnostic);
            Assert.That(result.Diagnostic, Does.StartWith("MX is not present, but A/AAAA is present"));
        });
    }

    /// <summary>
    /// A domain that publishes a null MX is refused, even though it has an address.
    /// </summary>
    /// <remarks>
    /// RFC 7505: a lone MX of <c>.</c> is a domain saying it accepts no mail, and it rules out the
    /// A/AAAA fallback. The check used to count that record as "MX is present" and pass the address,
    /// which is precisely the bounce <c>EmailSendController</c> runs this check to avoid — the
    /// sending domain's reputation is what pays for it.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_domain_that_publishes_a_null_mx_is_refused(CancellationToken ct = default)
    {
        await AssumeDnsAsync(NullMxDomain, ct);

        var result = await HostedMail.ValidateEMailDestination($"someone@{NullMxDomain}");

        Assert.Multiple(() =>
        {
            Assert.That(result.CanSendEmail, Is.False, result.Diagnostic);
            Assert.That(result.MxRecordsPresent, Is.False);
            Assert.That(result.Diagnostic, Does.Contain("null MX"));
            Assert.That(result.FailureReason, Is.Not.Null);
        });
    }

    /// <summary>
    /// With SMTP off — the suite's own configuration — every kind renders its template, is observed,
    /// and is journaled as not sent; the two codes a person types are also handed to the test store.
    /// </summary>
    /// <remarks>
    /// Through the hosted grain, so the one-way sends land when they land: the journal is polled until
    /// every kind is there. A body that is the render-failure marker would mean a form missing from
    /// <c>Resources</c>, which is the failure this is here to catch before a person does.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task With_smtp_off_every_kind_is_rendered_observed_and_journaled_as_not_sent(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var to      = session.Credentials.email;

        foreach (var kind in EveryKind)
            await kind.Send(HostedMail, to);

        var deadline = DateTimeOffset.UtcNow + AccountTimings.Slack * 5;
        var journal  = await Journal.ReadAsync(session.UserId, 0, 200, ct);

        while (EveryKind.Any(k => journal.Entries.All(e => e.Kind != k.Kind)) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, ct);
            journal = await Journal.ReadAsync(session.UserId, 0, 200, ct);
        }

        var otp   = await GetTestCodeStore().GetCodeAsync(to, TestCodeType.Email, AccountTimings.Slack, ct);
        var mails = AccountTimings.Emails.Sent(to);

        Assert.Multiple(() =>
        {
            foreach (var kind in EveryKind)
            {
                var entry = journal.Entries.FirstOrDefault(e => e.Kind == kind.Kind);
                var mail  = mails.FirstOrDefault(m => m.Kind == kind.Kind);

                Assert.That(entry?.Error, Is.EqualTo("smtp disabled"), $"{kind.Kind} was not journaled as not sent");
                Assert.That(mail, Is.Not.Null, $"{kind.Kind} was not observed");
                Assert.That(mail?.Body, Is.Not.Empty.And.Not.StartWith("<!-- template render failed"),
                    $"{kind.Kind} did not render");
            }

            Assert.That(otp, Is.EqualTo("654321").Or.EqualTo("123456"),
                "the codes a person types must reach the test store when there is no SMTP to carry them");
        });
    }

    // ── A second instance, SMTP on ───────────────────────────────────────────────────────────────

    /// <summary>
    /// With SMTP on, an address at a domain that accepts no mail is refused before any connection, for
    /// every kind, and the journal says so.
    /// </summary>
    /// <remarks>
    /// The null-MX domain rather than one that does not exist, because its answer is a positive one the
    /// resolver caches: a lookup that answers "no such domain" is the slow kind on a host with more than
    /// one resolver, since every resolver is asked before the answer is believed, and sixteen of them
    /// would be most of this fixture's time.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task With_smtp_on_an_address_that_cannot_receive_mail_is_refused_before_connecting(CancellationToken ct = default)
    {
        await AssumeDnsAsync(NullMxDomain, ct);

        var to     = $"argon.coverage.{Guid.NewGuid():N}@{NullMxDomain}";
        var userId = await RegisterAsync(to, ct);

        await using var smtp = new LoopbackSmtpServer(hangUp: true);

        var mail = SmtpOn(smtp.Port);

        foreach (var kind in EveryKind)
            await kind.Send(mail, to);

        var journal = await Journal.ReadAsync(userId, 0, 200, ct);

        Assert.Multiple(() =>
        {
            Assert.That(smtp.Connections, Is.Zero, "an address that failed the check was still taken to SMTP");

            foreach (var kind in EveryKind)
            {
                var entry = journal.Entries.FirstOrDefault(e => e.Kind == kind.Kind);

                Assert.That(entry, Is.Not.Null, $"{kind.Kind} left no line in the journal");
                Assert.That(entry?.Delivered, Is.False, kind.Kind);
                Assert.That(entry?.Error, Does.StartWith("address refused:"), kind.Kind);
            }
        });
    }

    /// <summary>
    /// With SMTP on, a server that hangs up is journaled as undelivered with its error, and only the
    /// two sends that answer a caller let it escape.
    /// </summary>
    /// <remarks>
    /// <para>Every one-way kind catches the failure after the journal has recorded it: there is nobody
    /// to hand the exception to, and Orleans would only log it again. The OTP send and the raw send do
    /// not — the raw send's failure is what makes <c>EmailSendController</c> answer 500 rather than
    /// hand the application a message id for a message that never left.</para>
    ///
    /// <para>Delivery itself — authentication, the SMTP dialogue, <c>delivered: true</c> — needs a
    /// server whose certificate this host trusts, and is the part this suite cannot reach.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task With_smtp_on_a_server_that_hangs_up_is_journaled_as_undelivered(CancellationToken ct = default)
    {
        await AssumeDnsAsync(DeliverableDomain, ct);

        var to     = $"argon.coverage.{Guid.NewGuid():N}@{DeliverableDomain}";
        var userId = await RegisterAsync(to, ct);

        await using var smtp = new LoopbackSmtpServer(hangUp: true);

        var mail   = SmtpOn(smtp.Port);
        var thrown = new List<string>();

        foreach (var kind in EveryKind)
        {
            try
            {
                await kind.Send(mail, to);
            }
            catch (Exception)
            {
                thrown.Add(kind.Kind);
            }
        }

        var journal = await Journal.ReadAsync(userId, 0, 200, ct);

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.EquivalentTo(new[] { EmailKinds.OtpCode, EmailKinds.Raw }),
                "only the sends with a caller to answer may let an SMTP failure escape");
            Assert.That(smtp.Connections, Is.GreaterThanOrEqualTo(EveryKind.Length),
                "every kind should have reached the SMTP server");

            foreach (var kind in EveryKind)
            {
                var entry = journal.Entries.FirstOrDefault(e => e.Kind == kind.Kind);

                Assert.That(entry, Is.Not.Null, $"{kind.Kind} left no line in the journal");
                Assert.That(entry?.Delivered, Is.False, kind.Kind);
                Assert.That(entry?.Error, Is.Not.Empty.And.Not.EqualTo("smtp disabled").And.Not.StartWith("address refused"),
                    $"{kind.Kind} should carry the SMTP failure");
            }
        });
    }

    /// <summary>
    /// With SMTP on, a send that runs out of time against a server that never answers is still
    /// journaled.
    /// </summary>
    /// <remarks>
    /// <para>The failure the journal most needs to see — a mail server that has stopped answering —
    /// and the one it used to lose. The send's ten-second budget was also the journal write's token, so
    /// by the time the catch block recorded the timeout the token had already fired, the write was
    /// cancelled, and the only trace was a warning that the journal could not be written. The journal
    /// now records with no token of its own: the outcome is known by then.</para>
    ///
    /// <para>Ten seconds of this test are the grain's own budget, which is not configurable.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task With_smtp_on_a_send_that_runs_out_of_time_is_still_journaled(CancellationToken ct = default)
    {
        await AssumeDnsAsync(DeliverableDomain, ct);

        var to     = $"argon.coverage.{Guid.NewGuid():N}@{DeliverableDomain}";
        var userId = await RegisterAsync(to, ct);

        await using var smtp = new LoopbackSmtpServer(hangUp: false);

        await SmtpOn(smtp.Port).SendResetCodeAsync(to, "000000", TimeSpan.FromMinutes(10));

        var entry = (await Journal.ReadAsync(userId, 0, 50, ct)).Entries
           .FirstOrDefault(e => e.Kind == EmailKinds.ResetCode);

        Assert.Multiple(() =>
        {
            Assert.That(smtp.Connections, Is.EqualTo(1), "premise: the send reached the silent server");
            Assert.That(entry, Is.Not.Null, "a send that timed out left no line in the journal");
            Assert.That(entry?.Delivered, Is.False);
            Assert.That(entry?.Error, Is.Not.Empty);
        });
    }

    /// <summary>
    /// Without an observer — the production configuration — nothing is observed and the send still
    /// journals.
    /// </summary>
    [Test, CancelAfter(60_000)]
    public async Task Without_an_observer_a_send_is_still_journaled(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var to      = session.Credentials.email;

        var mail = Build(new SmtpConfig { Enabled = false }, observe: false);

        await mail.SendDeletionCancelledAsync(to, "Unobserved");

        var entry = (await Journal.ReadAsync(session.UserId, 0, 50, ct)).Entries
           .FirstOrDefault(e => e.Kind == EmailKinds.DeletionCancelled);

        Assert.Multiple(() =>
        {
            Assert.That(AccountTimings.Emails.Sent(to), Is.Empty, "a manager with no sink observed a message");
            Assert.That(entry?.Error, Is.EqualTo("smtp disabled"));
        });
    }

    /// <summary>
    /// A template that cannot be rendered is observed as a marker and does not stop the send.
    /// </summary>
    /// <remarks>
    /// The observer is observation: a form missing from <c>Resources</c> must not turn a send that
    /// would have been journaled into an exception from the sink's side of the method.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_template_that_fails_to_render_is_observed_as_a_marker(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var to      = session.Credentials.email;

        var mail = Build(new SmtpConfig { Enabled = false }, forms: new EMailFormStorage());

        await mail.SendExportStartedAsync(to, "No Forms");

        var observed = AccountTimings.Emails.Sent(to, EmailKinds.ExportStarted);
        var entry    = (await Journal.ReadAsync(session.UserId, 0, 50, ct)).Entries
           .FirstOrDefault(e => e.Kind == EmailKinds.ExportStarted);

        Assert.Multiple(() =>
        {
            Assert.That(observed, Has.Count.EqualTo(1));
            Assert.That(observed.FirstOrDefault()?.Body, Does.StartWith("<!-- template render failed:").And.Contain("export_started"));
            Assert.That(entry?.Error, Is.EqualTo("smtp disabled"), "the send stopped because its template did");
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every template the grain sends, with arguments that render it.</summary>
    private static readonly (string Kind, Func<IEmailManager, string, Task> Send)[] EveryKind =
    [
        (EmailKinds.OtpCode,           (m, to) => m.SendOtpCodeAsync(to, "123456", TimeSpan.FromMinutes(10))),
        (EmailKinds.ResetCode,         (m, to) => m.SendResetCodeAsync(to, "654321", TimeSpan.FromMinutes(10))),
        (EmailKinds.PasswordChanged,   (m, to) => m.SendNotificationResetPasswordAsync(to)),
        (EmailKinds.DeleteNotice,      (m, to) => m.SendDeleteNoticeAsync(to, "Coverage", DateTimeOffset.UtcNow.AddDays(30))),
        (EmailKinds.MagicLink,         (m, to) => m.SendMagicLinkAsync(to, "https://app.test.local/magic", AppName, TimeSpan.FromMinutes(15))),
        (EmailKinds.RegistrationInvite,(m, to) => m.SendRegistrationInviteAsync(to, "https://app.test.local/invite", AppName, TimeSpan.FromHours(48))),
        (EmailKinds.Raw,               (m, to) => m.SendRawAsync(to, "Raw coverage", "<p>raw</p>", null, "reply@test.local")),
        (EmailKinds.ExportStarted,     (m, to) => m.SendExportStartedAsync(to, "Coverage")),
        (EmailKinds.ExportReady,       (m, to) => m.SendExportReadyAsync(to, "Coverage", "https://files.test.local/export.zip")),
        (EmailKinds.ExportFailed,      (m, to) => m.SendExportFailedAsync(to, "Coverage")),
        (EmailKinds.DeletionScheduled, (m, to) => m.SendDeletionScheduledAsync(to, "Coverage", DateTimeOffset.UtcNow.AddDays(30))),
        (EmailKinds.DeletionReminder,  (m, to) => m.SendDeletionReminderAsync(to, "Coverage", 7)),
        (EmailKinds.DeletionCompleted, (m, to) => m.SendDeletionCompletedAsync(to, "Coverage")),
        (EmailKinds.DeletionCancelled, (m, to) => m.SendDeletionCancelledAsync(to, "Coverage")),
        (EmailKinds.DeletionCancelledBySignIn,
            (m, to) => m.SendDeletionCancelledBySignInAsync(to, "Coverage", "203.0.113.7", "Nowhere", "Coverage client", DateTimeOffset.UtcNow)),
        (EmailKinds.NewDeviceSignIn,
            (m, to) => m.SendNewDeviceSignInAsync(to, "Coverage", "203.0.113.7", "Nowhere", "Coverage client", DateTimeOffset.UtcNow))
    ];

    /// <summary>An <see cref="EmailManager"/> with SMTP on, pointed at a loopback port.</summary>
    private EmailManager SmtpOn(int port)
        => Build(new SmtpConfig
        {
            Enabled  = true,
            Host     = IPAddress.Loopback.ToString(),
            Port     = port,
            User     = "noreply@argon.gl",
            Password = "not-a-password",
            UseSsl   = true
        });

    /// <summary>The mail grain built from the host's own services, with the configuration given.</summary>
    private EmailManager Build(SmtpConfig smtp, bool observe = true, EMailFormStorage? forms = null)
    {
        var services = FactoryAsp.Services;

        return new EmailManager(
            Options.Create(smtp),
            services.GetRequiredService<ILogger<EmailManager>>(),
            forms ?? services.GetRequiredService<EMailFormStorage>(),
            observe ? services.GetServices<IEmailSink>() : [],
            services.GetRequiredService<IEmailJournal>());
    }

    /// <summary>
    /// The newest journal line of one kind, for one account or — with <see langword="null"/> — for the
    /// platform, waiting briefly for a one-way send to land.
    /// </summary>
    private async Task<EmailJournalRecord?> JournaledAsync(
        Guid? userId, string kind, CancellationToken ct, DateTimeOffset? since = null)
    {
        var deadline = DateTimeOffset.UtcNow + AccountTimings.Slack;

        while (true)
        {
            var page  = await Journal.ReadAsync(userId, 0, 200, ct);
            var entry = page.Entries.FirstOrDefault(e => e.Kind == kind && (since is null || e.SentAt >= since));

            if (entry is not null || DateTimeOffset.UtcNow >= deadline)
                return entry;

            await Task.Delay(50, ct);
        }
    }

    /// <summary>Registers an account at a given address, so the journal can scope its mail to it.</summary>
    private async Task<Guid> RegisterAsync(string email, CancellationToken ct)
    {
        var creds = GenerateCredentials() with { email = email };

        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var result = await IonClient.ForService<IIdentityInteraction>(scope.ServiceProvider).Registration(
            new NewUserCredentialsInput(
                creds.email,
                creds.username,
                creds.password,
                creds.displayName,
                creds.argreeTos,
                creds.birthDate,
                creds.argreeOptionalEmails,
                creds.captchaToken,
                "1.0",
                "1.0"),
            ct);

        if (result is not SuccessRegistration registered)
            throw new AssertionException($"registration at {email} failed: {(result as FailedRegistration)?.error}");

        SetAuthToken(registered.token);

        try
        {
            return (await GetUserService(scope.ServiceProvider).GetMe(ct)).userId;
        }
        finally
        {
            ResetAuthentication();
        }
    }

    /// <summary>Stands the test down when this host cannot resolve the domain it depends on.</summary>
    private static async Task AssumeDnsAsync(string domain, CancellationToken ct)
    {
        IPAddress[] addresses;

        try
        {
            addresses = await Dns.GetHostAddressesAsync(domain, ct);
        }
        catch (SocketException e)
        {
            addresses = [];
            TestContext.Out.WriteLine($"{domain} does not resolve here: {e.Message}");
        }

        Assume.That(addresses, Is.Not.Empty, $"this host cannot resolve {domain}; the check under test needs DNS");
    }

    /// <summary>
    /// A loopback "SMTP server" that either hangs up on every connection before the TLS handshake, or
    /// holds it open and never says a word.
    /// </summary>
    private sealed class LoopbackSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener             listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop     = new();
        private readonly List<TcpClient>         held     = [];
        private readonly bool                    hangUp;
        private readonly Task                    accepting;
        private int                              connections;

        public LoopbackSmtpServer(bool hangUp)
        {
            this.hangUp = hangUp;
            listener.Start();
            accepting = AcceptAsync();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public int Connections => Volatile.Read(ref connections);

        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);

                    Interlocked.Increment(ref connections);

                    if (hangUp)
                        client.Dispose();
                    else
                        held.Add(client);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // stopped
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await accepting;

            foreach (var client in held)
                client.Dispose();

            stop.Dispose();
        }
    }
}
