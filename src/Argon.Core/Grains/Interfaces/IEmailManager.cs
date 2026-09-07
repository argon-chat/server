namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

public record SmtpConfig
{
    public string Host     { get; set; }
    public int    Port     { get; set; }
    public string User     { get; set; }
    public string Password { get; set; }
    public bool   UseSsl   { get; set; }
    public bool   Enabled  { get; set; }
}

[Alias("Argon.Grains.Interfaces.IEmailManager")]
public interface IEmailManager : IGrainWithGuidKey
{
    [Alias(nameof(SendOtpCodeAsync)), OneWay]
    Task SendOtpCodeAsync(string email, string otpCode, TimeSpan validity);

    [Alias(nameof(SendResetCodeAsync)), OneWay]
    Task SendResetCodeAsync(string email, string otpCode, TimeSpan validity);

    [Alias(nameof(SendNotificationResetPasswordAsync)), OneWay]
    Task SendNotificationResetPasswordAsync(string email);

    [Alias(nameof(SendDeleteNoticeAsync)), OneWay]
    Task SendDeleteNoticeAsync(string email, string displayName, DateTimeOffset deletionTime);

    [Alias(nameof(SendMagicLinkAsync)), OneWay]
    Task SendMagicLinkAsync(string email, string link, string appName, TimeSpan validity);

    [Alias(nameof(SendRegistrationInviteAsync)), OneWay]
    Task SendRegistrationInviteAsync(string email, string link, string appName, TimeSpan validity);

    [Alias(nameof(SendRawAsync))]
    Task<string> SendRawAsync(string to, string subject, string html, string? from, string? replyTo);

    [Alias(nameof(SendExportStartedAsync)), OneWay]
    Task SendExportStartedAsync(string email, string displayName);

    [Alias(nameof(SendExportReadyAsync)), OneWay]
    Task SendExportReadyAsync(string email, string displayName, string downloadUrl);

    /// <summary>The third outcome of an export, and the one that had no mail (defect X7).</summary>
    /// <remarks>
    /// Started and ready were the whole conversation, so an export that threw part-way left the
    /// person who asked for their data waiting on a promise the product had put in writing. No
    /// failure reason travels with it: what a person can act on is that it did not work and that
    /// asking again is allowed.
    /// </remarks>
    [Alias(nameof(SendExportFailedAsync)), OneWay]
    Task SendExportFailedAsync(string email, string displayName);

    [Alias(nameof(SendDeletionScheduledAsync)), OneWay]
    Task SendDeletionScheduledAsync(string email, string displayName, DateTimeOffset deletionDate);

    [Alias(nameof(SendDeletionReminderAsync)), OneWay]
    Task SendDeletionReminderAsync(string email, string displayName, int daysRemaining);

    [Alias(nameof(SendDeletionCompletedAsync)), OneWay]
    Task SendDeletionCompletedAsync(string email, string displayName);

    [Alias(nameof(SendDeletionCancelledAsync)), OneWay]
    Task SendDeletionCancelledAsync(string email, string displayName);

    /// <summary>
    /// An inactivity deletion was called off because the account signed in. Names the sign-in — address,
    /// location, client, time — so a person who did not sign in knows somebody else has their password.
    /// </summary>
    [Alias(nameof(SendDeletionCancelledBySignInAsync)), OneWay]
    Task SendDeletionCancelledBySignInAsync(string email, string displayName, string ip, string location, string client, DateTimeOffset at);

    /// <summary>The account was signed in from a device it had never been seen on. Same evidence, same reason.</summary>
    [Alias(nameof(SendNewDeviceSignInAsync)), OneWay]
    Task SendNewDeviceSignInAsync(string email, string displayName, string ip, string location, string client, DateTimeOffset at);

    [Alias(nameof(ValidateEMailDestination))]
    Task<EmailValidationResult> ValidateEMailDestination(string email, CancellationToken ct = default);
}

public sealed record EmailValidationResult(
    bool SyntaxValid,
    string? NormalizedAddress,
    string? DomainPunycode,
    bool DomainResolves,
    bool MxRecordsPresent,
    SmtpCheckStatus SmtpStatus,
    string? Diagnostic
)
{
    public bool CanSendEmail =>
        SyntaxValid &&
        DomainResolves &&
        (MxRecordsPresent || DomainResolves) &&
        (SmtpStatus is SmtpCheckStatus.NotPerformed or SmtpCheckStatus.Accepted);

    public string? FailureReason
    {
        get
        {
            if (!SyntaxValid) return "Invalid email syntax.";
            if (!DomainResolves) return "Domain does not resolve (no A/AAAA records).";
            if (!MxRecordsPresent && !DomainResolves) return "Domain has no MX and no valid fallback (A/AAAA).";
            if (SmtpStatus is SmtpCheckStatus.Rejected) return "SMTP server rejected recipient (550/551/553).";
            if (SmtpStatus is SmtpCheckStatus.TemporaryFailure) return "SMTP temporary failure (4xx).";
            if (SmtpStatus is SmtpCheckStatus.CouldNotConnect) return "Could not connect to any SMTP server.";
            if (SmtpStatus is SmtpCheckStatus.Inconclusive) return "SMTP check was inconclusive.";

            return null; // means CanSendEmail == true
        }
    }
}

public enum SmtpCheckStatus
{
    NotPerformed = 0,
    Accepted,
    TemporaryFailure,
    Rejected,
    CouldNotConnect,
    Inconclusive
}