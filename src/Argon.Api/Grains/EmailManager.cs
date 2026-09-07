namespace Argon.Grains;

using System.Globalization;
using System.Net.Mail;
using System.Threading;
using DnsClient;
using DnsClient.Protocol;
using Features.Template;
using Features.Testing;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Orleans.Concurrency;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

[StatelessWorker]
public class EmailManager(
    IOptions<SmtpConfig> smtpOptions, 
    ILogger<EmailManager> logger, 
    EMailFormStorage formStorage,
    IEnumerable<IEmailSink> emailSinks,
    ITestCodeStore? testCodeStore = null) : Grain, IEmailManager
{
    /// <summary>
    /// The observer a test host registered, or nothing.
    /// </summary>
    /// <remarks>
    /// Injected as a sequence rather than as an optional <c>IEmailSink?</c>, and the difference is
    /// not style. An optional parameter is resolvable by <c>ActivatorUtilities</c> but not by the
    /// container on its own, and <c>RoleStartupTests.A_silo_role_can_construct_every_grain_it_hosts</c>
    /// asks the container: every constructor parameter of every grain a role hosts has to resolve to
    /// something, which is the check that catches a grain quietly unbuildable on the one role that
    /// hosts it. A sequence always resolves — empty when nothing is registered — so the hook stays
    /// genuinely absent in production without registering a null object there, and without the check
    /// having to be taught about defaults.
    /// </remarks>
    private readonly IEmailSink? emailSink = emailSinks.FirstOrDefault();

    /// <summary>
    /// Hands one outgoing message to <see cref="IEmailSink"/>, when a host registered one.
    /// </summary>
    /// <remarks>
    /// <para>Called at the top of every <c>Send*</c> method, ahead of the disabled-SMTP early return
    /// and ahead of the MX validation inside <see cref="SendAsync"/> - both of which are taken in any
    /// host without a mail server, which is every host a test runs in. Recording after either would
    /// record nothing.</para>
    ///
    /// <para>The body is a factory rather than a string because the send path renders the template
    /// <em>after</em> the enabled check, and rendering it here unconditionally would move that work
    /// onto a production host that is about to discard it. With no sink registered the delegate is
    /// never invoked and this is a null check.</para>
    ///
    /// <para>A template that throws is reported as a marker rather than propagated: the sink is
    /// observation, and observation must not be able to fail a send that would otherwise have gone
    /// out.</para>
    /// </remarks>
    private void Observe(string to, string kind, string subject, Func<string> body)
    {
        if (emailSink is null)
            return;

        string rendered;

        try
        {
            rendered = body();
        }
        catch (Exception e)
        {
            rendered = $"<!-- template render failed: {e.Message} -->";
        }

        emailSink.Record(to, kind, subject, rendered);
    }

    private MimeMessage CreateMessage(string to, string subject, string bodyHtml)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(smtpOptions.Value.User));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        message.Body = new TextPart("html")
        {
            Text = bodyHtml
        };

        return message;
    }

    private async Task SendAsync(string email, MimeMessage message, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateEMailDestination(email, cancellationToken);

        if (!validation.CanSendEmail)
        {
            logger.LogError("Failed send email to {email}, validation failed, {reason}", email, validation.FailureReason);
            return;
        }


        using var client = new SmtpClient();
        message.MessageId = $"{message.MessageId?.Split('@').First()}@argon.gl";
        try
        {
            var options = smtpOptions.Value;

            await client.ConnectAsync(options.Host, options.Port, SecureSocketOptions.SslOnConnect, cancellationToken);
            await client.AuthenticateAsync(options.User, options.Password, cancellationToken);
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send email to {To}", message.To);
            throw;
        }
    }

    public Task SendEmailAsync(string email, string subject, string message, string template = "none")
    {
        Observe(email, EmailKinds.Generic, subject, () => message);

        var msg = CreateMessage(email, subject, message);
        return SendAsync(email, msg);
    }

    public async Task SendOtpCodeAsync(string email, string otpCode, TimeSpan validity)
    {
        Observe(email, EmailKinds.OtpCode, "Your Argon verification code",
            () => formStorage.Render("otp", new Dictionary<string, string>
            {
                { "otp", otpCode },
                { "validity", $"{(int)Math.Floor(validity.TotalMinutes):D}" }
            }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[OTP CODE]: {Email}, code: {OtpCode}", email, otpCode);
            testCodeStore?.StoreCode(email, otpCode, TestCodeType.Email);
            return;
        }

        var form = formStorage.Render("otp", new Dictionary<string, string>
        {
            {
                "otp", otpCode
            },
            {
                "validity", $"{(int)Math.Floor(validity.TotalMinutes):D}"
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var       msg = CreateMessage(email, "Your Argon verification code", form);
        await SendAsync(email, msg, cts.Token);
    }

    public async Task SendResetCodeAsync(string email, string otpCode, TimeSpan validity)
    {
        Observe(email, EmailKinds.ResetCode, "Your Argon reset password code",
            () => formStorage.Render("reset_pass", new Dictionary<string, string> { { "reset_code", otpCode } }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[OTP RESET CODE]: {Email}, code: {OtpCode}", email, otpCode);
            testCodeStore?.StoreCode(email, otpCode, TestCodeType.Email);
            return;
        }

        var form = formStorage.Render("reset_pass", new Dictionary<string, string>
        {
            {
                "reset_code", otpCode
            }
        });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var       msg = CreateMessage(email, "Your Argon reset password code", form);
            await SendAsync(email, msg, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send reset code to '{email}'", email);
        }
    }

    [OneWay]
    public async Task SendDeleteNoticeAsync(string email, string displayName, DateTimeOffset deletionTime)
    {
        Observe(email, EmailKinds.DeleteNotice, "Account Deletion Notice",
            () => formStorage.Render("deletion_notice", new Dictionary<string, string>
            {
                { "deletion_date", deletionTime.ToString("D") },
                { "displayName", displayName }
            }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[NOTIFICATION ABOUT RESET PASS]: {Email}", email);
            return;
        }

        var form = formStorage.Render("deletion_notice", new Dictionary<string, string>
        {
            {
                "deletion_date", deletionTime.ToString("D")
            },
            {
                "displayName", displayName
            },
        });

        try
        {
            //using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var msg = CreateMessage(email, "Account Deletion Notice", form);
            await SendAsync(email, msg, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send reset code to '{email}'", email);
        }
    }

    [OneWay]
    public async Task SendMagicLinkAsync(string email, string link, string appName, TimeSpan validity)
    {
        Observe(email, EmailKinds.MagicLink, $"Sign in to {appName}",
            () => formStorage.Render("magic_link", new Dictionary<string, string>
            {
                { "link", link },
                { "app_name", appName },
                { "validity", $"{(int)Math.Floor(validity.TotalMinutes):D}" }
            }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[MAGIC LINK]: {Email}, link: {Link}", email, link);
            return;
        }

        var form = formStorage.Render("magic_link", new Dictionary<string, string>
        {
            { "link", link },
            { "app_name", appName },
            { "validity", $"{(int)Math.Floor(validity.TotalMinutes):D}" }
        });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var msg = CreateMessage(email, $"Sign in to {appName}", form);
            await SendAsync(email, msg, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send magic link to '{email}'", email);
        }
    }

    [OneWay]
    public async Task SendRegistrationInviteAsync(string email, string link, string appName, TimeSpan validity)
    {
        Observe(email, EmailKinds.RegistrationInvite, $"You have been invited to {appName}",
            () => formStorage.Render("invite_register", new Dictionary<string, string>
            {
                { "link", link },
                { "app_name", appName },
                { "validity", $"{(int)Math.Floor(validity.TotalHours):D}" }
            }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[INVITE REGISTER]: {Email}, link: {Link}", email, link);
            return;
        }

        var form = formStorage.Render("invite_register", new Dictionary<string, string>
        {
            { "link", link },
            { "app_name", appName },
            { "validity", $"{(int)Math.Floor(validity.TotalHours):D}" }
        });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var msg = CreateMessage(email, $"You've been invited to {appName}", form);
            await SendAsync(email, msg, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send registration invite to '{email}'", email);
        }
    }

    public async Task<string> SendRawAsync(string to, string subject, string html, string? from, string? replyTo)
    {
        Observe(to, EmailKinds.Raw, subject, () => html);

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[RAW EMAIL]: to={To}, subject={Subject}", to, subject);
            return $"{Guid.NewGuid()}@argon.gl";
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from ?? smtpOptions.Value.User));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        if (!string.IsNullOrEmpty(replyTo))
            message.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        message.Body = new TextPart("html") { Text = html };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await SendAsync(to, message, cts.Token);

        var messageId = message.MessageId ?? $"{Guid.NewGuid()}@argon.gl";
        logger.LogInformation("[RAW EMAIL] Sent to={To}, subject={Subject}, messageId={MessageId}", to, subject, messageId);
        return messageId;
    }

    public async Task<EmailValidationResult> ValidateEMailDestination(string email, CancellationToken ct = default)
    {
        string addressLocalPart;
        string domainRaw;

        try
        {
            var parsed = new MailAddress(email);
            var addr   = parsed.Address;
            var at     = addr.LastIndexOf('@');
            if (at <= 0 || at == addr.Length - 1)
                return new EmailValidationResult(false, null, null, false, false, SmtpCheckStatus.NotPerformed, "There is no local part or domain");

            addressLocalPart = addr[..at];
            domainRaw        = addr[(at + 1)..];
        }
        catch (Exception ex)
        {
            return new EmailValidationResult(false, null, null, false, false, SmtpCheckStatus.NotPerformed, $"Syntax error: {ex.Message}");
        }

        string domainAscii;
        try
        {
            var idn = new IdnMapping();
            var labels = domainRaw.Replace('。', '.').Replace('．', '.').Replace('｡', '.')
               .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (labels.Length == 0) throw new ArgumentException("Empty domain after normalization.");
            domainAscii = string.Join(".", labels.Select(l => idn.GetAscii(l)));
        }
        catch (Exception ex)
        {
            return new EmailValidationResult(
                true,
                null,
                null,
                false,
                false,
                SmtpCheckStatus.NotPerformed,
                $"The domain does not match the IDNA: {ex.Message}"
            );
        }

        var normalizedAddress = $"{addressLocalPart}@{domainAscii}";

        var lookup = new LookupClient(new LookupClientOptions
        {
            Timeout = TimeSpan.FromMilliseconds(400),
            Retries = 1
        });

        var mx = Array.Empty<MxRecord>();
        try
        {
            var mxResp = await lookup.QueryAsync(domainAscii, QueryType.MX, cancellationToken: ct);
            mx = mxResp.Answers.MxRecords().OrderBy(r => r.Preference).ToArray();
        }
        catch
        {
            // ignored
        }

        var mxPresent      = mx.Length > 0;
        var domainResolves = false;

        try
        {
            var a    = await lookup.QueryAsync(domainAscii, QueryType.A, cancellationToken: ct);
            var aaaa = await lookup.QueryAsync(domainAscii, QueryType.AAAA, cancellationToken: ct);
            domainResolves = a.Answers.ARecords().Any() || aaaa.Answers.AaaaRecords().Any();
        }
        catch
        {
            // ignored
        }

        if (!mxPresent && !domainResolves)
        {
            return new EmailValidationResult(
                true,
                normalizedAddress,
                domainAscii,
                false,
                false,
                SmtpCheckStatus.NotPerformed,
                "The domain does not have an MX and it does not resolve to A/AAAA"
            );
        }

        return new EmailValidationResult(
            true,
            normalizedAddress,
            domainAscii,
            domainResolves,
            mxPresent,
            SmtpCheckStatus.NotPerformed,
            mxPresent ? "MX is present" : "MX is not present, but A/AAAA is present (delivery by RFC-fallback is possible)"
        );
    }

    [OneWay]
    public async Task SendNotificationResetPasswordAsync(string email)
    {
        Observe(email, EmailKinds.PasswordChanged, "Your Argon password changed",
            () => formStorage.Render("pass_changed", new Dictionary<string, string>()));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[NOTIFICATION ABOUT RESET PASS]: {Email}", email);
            return;
        }

        try
        {
            using var cts  = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var       form = formStorage.Render("pass_changed", new Dictionary<string, string>());
            var       msg  = CreateMessage(email, "Your Argon password changed", form);
            await SendAsync(email, msg, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send notification to '{email}'", email);
        }
    }

    [OneWay]
    public async Task SendExportStartedAsync(string email, string displayName)
    {
        Observe(email, EmailKinds.ExportStarted, "Your data export has started",
            () => formStorage.Render("export_started", new Dictionary<string, string> { { "displayName", displayName } }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[EXPORT STARTED]: {Email}", email);
            return;
        }

        var form = formStorage.Render("export_started", new Dictionary<string, string>
        {
            { "displayName", displayName }
        });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var msg = CreateMessage(email, "Your data export has started", form);
            await SendAsync(email, msg, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send export started email to '{email}'", email);
        }
    }

    [OneWay]
    public async Task SendExportReadyAsync(string email, string displayName, string downloadUrl)
    {
        Observe(email, EmailKinds.ExportReady, "Your data export is ready",
            () => formStorage.Render("export_ready", new Dictionary<string, string>
            {
                { "displayName", displayName },
                { "downloadUrl", downloadUrl }
            }));

        if (!smtpOptions.Value.Enabled)
        {
            // The address only. The presigned GET is an unauthenticated capability over the whole
            // archive — profile.json carries the e-mail, phone and date of birth, devices.json up to a
            // hundred IP addresses, and every message the person wrote — good for the archive's full
            // TTL, and log read access is a wider group than object-store read access (defect R8).
            // Nothing operational is lost: the owner reads the same link from GetDataExportStatus.
            logger.LogWarning("[EXPORT READY]: {Email}", email);
            return;
        }

        var form = formStorage.Render("export_ready", new Dictionary<string, string>
        {
            { "displayName", displayName },
            { "downloadUrl", downloadUrl }
        });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var msg = CreateMessage(email, "Your data export is ready", form);
            await SendAsync(email, msg, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send export ready email to '{email}'", email);
        }
    }

    [OneWay]
    public async Task SendExportFailedAsync(string email, string displayName)
    {
        Observe(email, EmailKinds.ExportFailed, "Your data export could not be completed",
            () => formStorage.Render("export_failed", new Dictionary<string, string> { { "displayName", displayName } }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[EXPORT FAILED]: {Email}", email);
            return;
        }

        var form = formStorage.Render("export_failed", new Dictionary<string, string>
        {
            { "displayName", displayName }
        });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var msg = CreateMessage(email, "Your data export could not be completed", form);
            await SendAsync(email, msg, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send export failed email to '{email}'", email);
        }
    }

    [OneWay]
    public async Task SendDeletionScheduledAsync(string email, string displayName, DateTimeOffset deletionDate)
    {
        Observe(email, EmailKinds.DeletionScheduled, "Account Deletion Scheduled",
            () => formStorage.Render("deletion_scheduled", new Dictionary<string, string>
            {
                { "displayName", displayName },
                { "deletion_date", deletionDate.ToString("D") }
            }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[DELETION SCHEDULED]: {Email}, date: {Date}", email, deletionDate);
            return;
        }

        var form = formStorage.Render("deletion_scheduled", new Dictionary<string, string>
        {
            { "displayName", displayName },
            { "deletion_date", deletionDate.ToString("D") }
        });

        try
        {
            var msg = CreateMessage(email, "Account Deletion Scheduled", form);
            await SendAsync(email, msg, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send deletion scheduled email to '{email}'", email);
        }
    }

    [OneWay]
    public async Task SendDeletionReminderAsync(string email, string displayName, int daysRemaining)
    {
        Observe(email, EmailKinds.DeletionReminder, $"Account Deletion in {daysRemaining} Day(s)",
            () => formStorage.Render("deletion_reminder", new Dictionary<string, string>
            {
                { "displayName", displayName },
                { "days_remaining", daysRemaining.ToString() }
            }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[DELETION REMINDER]: {Email}, days: {Days}", email, daysRemaining);
            return;
        }

        var form = formStorage.Render("deletion_reminder", new Dictionary<string, string>
        {
            { "displayName", displayName },
            { "days_remaining", daysRemaining.ToString() }
        });

        try
        {
            var msg = CreateMessage(email, $"Account Deletion in {daysRemaining} Day(s)", form);
            await SendAsync(email, msg, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send deletion reminder email to '{email}'", email);
        }
    }

    [OneWay]
    public async Task SendDeletionCompletedAsync(string email, string displayName)
    {
        Observe(email, EmailKinds.DeletionCompleted, "Your Account Has Been Deleted",
            () => formStorage.Render("deletion_completed", new Dictionary<string, string> { { "displayName", displayName } }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[DELETION COMPLETED]: {Email}", email);
            return;
        }

        var form = formStorage.Render("deletion_completed", new Dictionary<string, string>
        {
            { "displayName", displayName }
        });

        try
        {
            var msg = CreateMessage(email, "Your Account Has Been Deleted", form);
            await SendAsync(email, msg, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send deletion completed email to '{email}'", email);
        }
    }

    [OneWay]
    public async Task SendDeletionCancelledAsync(string email, string displayName)
    {
        Observe(email, EmailKinds.DeletionCancelled, "Account Deletion Cancelled",
            () => formStorage.Render("deletion_cancelled", new Dictionary<string, string> { { "displayName", displayName } }));

        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[DELETION CANCELLED]: {Email}", email);
            return;
        }

        var form = formStorage.Render("deletion_cancelled", new Dictionary<string, string>
        {
            { "displayName", displayName }
        });

        try
        {
            var msg = CreateMessage(email, "Account Deletion Cancelled", form);
            await SendAsync(email, msg, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send deletion cancelled email to '{email}'", email);
        }
    }
}