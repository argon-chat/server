namespace Argon.Api.Grains;

using System.Net;
using System.Net.Mail;
using Interfaces;
using Microsoft.Extensions.Options;

public class EmailManager(IOptions<SmtpConfig> config) : Grain, IEmailManager
{
    private SmtpClient Client => new()
    {
        Port                  = config.Value.Port,
        Host                  = config.Value.Host,
        EnableSsl             = true,
        DeliveryMethod        = SmtpDeliveryMethod.Network,
        UseDefaultCredentials = false,
        Credentials           = new NetworkCredential(config.Value.User, config.Value.Password)
    };

    public Task SendEmailAsync(string email, string subject, string message, string template = "none")
    {
        var mail = new MailMessage(config.Value.User, email, subject, message)
        {
            IsBodyHtml = true
        };
        return Client.SendMailAsync(mail);
    }
}