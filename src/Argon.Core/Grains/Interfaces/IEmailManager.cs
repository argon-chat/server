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