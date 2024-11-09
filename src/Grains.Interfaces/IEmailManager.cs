namespace Grains.Interfaces;

using Orleans;

public record SmtpConfig
{
    public string Host     { get; set; }
    public int    Port     { get; set; }
    public string User     { get; set; }
    public string Password { get; set; }
    public bool   UseSsl   { get; set; }
}

public interface IEmailManager : IGrainWithGuidKey
{
    Task SendEmailAsync(string email, string subject, string message, string template = "none");
    Task SendOtpCodeAsync(string email, string otpCode, TimeSpan validity);
}