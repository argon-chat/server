namespace Argon.Grains;

using System.Net;
using System.Net.Mail;
using Features.Template;

public class EmailManager(IOptions<SmtpConfig> smtpOptions, ILogger<EmailManager> logger, EMailFormStorage formStorage) : Grain, IEmailManager
{
    private SmtpClient Client => new()
    {
        Port                  = smtpOptions.Value.Port,
        Host                  = smtpOptions.Value.Host,
        EnableSsl             = smtpOptions.Value.UseSsl,
        DeliveryMethod        = SmtpDeliveryMethod.Network,
        UseDefaultCredentials = false,
        Credentials           = new NetworkCredential(smtpOptions.Value.User, smtpOptions.Value.Password)
    };

    public Task SendEmailAsync(string email, string subject, string message, string template = "none") =>
        Client.SendMailAsync(new MailMessage(smtpOptions.Value.User, email, subject, message)
        {
            IsBodyHtml = true
        });

    public async Task SendOtpCodeAsync(string email, string otpCode, TimeSpan validity)
    {
        var form = formStorage.Render("otp", new Dictionary<string, string>
        {
            {
                "otp", otpCode
            },
            {
                "validity", $"{(int)Math.Floor(validity.TotalMinutes):D}"
            }
        });

        await Client.SendMailAsync(new MailMessage(smtpOptions.Value.User, email, $"Your Argon verification code", form)
        {
            IsBodyHtml = true
        });
    }
}