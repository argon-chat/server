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
}