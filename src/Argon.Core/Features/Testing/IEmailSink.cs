namespace Argon.Features.Testing;

/// <summary>
/// Somewhere for an outgoing e-mail to be observed before it is handed to SMTP.
/// </summary>
/// <remarks>
/// <para>Optional and absent in production, in the spirit of <see cref="ITestCodeStore"/>: nothing
/// registers an implementation on any shipped role and every call site is a null-conditional that
/// costs one branch. <c>EmailManager</c> takes it as an <c>IEnumerable&lt;IEmailSink&gt;</c> rather
/// than as an optional parameter, because a sequence resolves to an empty one when nothing is
/// registered — which keeps the hook absent in production without a null object standing in for it,
/// and keeps the grain constructible by the container itself, which is what
/// <c>RoleStartupTests.A_silo_role_can_construct_every_grain_it_hosts</c> checks.</para>
///
/// <para><b>Why it has to exist at all.</b> An account being scheduled for deletion, reminded about
/// it, deleted, or handed a finished export are all events whose <em>entire</em> user-visible output
/// is an e-mail — the grain writes state and sends mail, and the mail is the half a person actually
/// experiences. In the integration host there is no SMTP server and the suite's addresses are under
/// <c>.local</c>, which resolves to nothing, so <c>EmailManager</c> takes the disabled-SMTP branch or
/// fails MX validation and returns; either way it logs and drops the message. Nothing observes it,
/// so "the reminder is sent exactly once per threshold" and "the ready mail carries the download
/// link" were assertions with nowhere to land.</para>
///
/// <para>Recorded <em>before</em> the send is attempted rather than after it succeeds, because in a
/// test host no send ever succeeds. What is under test is the decision to send — who is written to,
/// which of the templates it is, and what went into it — not the delivery, which belongs to a mail
/// server and not to this product.</para>
/// </remarks>
public interface IEmailSink
{
    /// <summary>
    /// One outgoing message, at the point the grain decided to send it.
    /// </summary>
    /// <param name="to">The recipient address, as the grain had it.</param>
    /// <param name="kind">
    /// A stable identifier for the template — <c>deletion-scheduled</c>, <c>export-ready</c> and so
    /// on, one per <c>Send*Async</c> method. Stable because it is what a test matches on: the subject
    /// is prose that may be reworded and the body is rendered HTML, but the kind names the decision.
    /// </param>
    /// <param name="subject">The subject line as it would have been sent.</param>
    /// <param name="body">The rendered body, or a short marker if rendering it threw.</param>
    void Record(string to, string kind, string subject, string body);
}

/// <summary>The stable <c>kind</c> strings, so a sink and a test cannot disagree about spelling.</summary>
public static class EmailKinds
{
    public const string OtpCode                 = "otp-code";
    public const string ResetCode               = "reset-code";
    public const string PasswordChanged         = "password-changed";
    public const string MagicLink               = "magic-link";
    public const string RegistrationInvite      = "registration-invite";
    public const string Raw                     = "raw";
    public const string Generic                 = "generic";
    public const string DeleteNotice            = "delete-notice";
    public const string DeletionScheduled       = "deletion-scheduled";
    public const string DeletionReminder        = "deletion-reminder";
    public const string DeletionCancelled       = "deletion-cancelled";
    public const string DeletionCompleted       = "deletion-completed";
    public const string ExportStarted           = "export-started";
    public const string ExportReady             = "export-ready";
    public const string ExportFailed            = "export-failed";
}
