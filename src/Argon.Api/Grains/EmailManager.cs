namespace Argon.Grains;

using Argon.Features.Otp;
using Features.Template;
using Orleans.Concurrency;
using System.Net;
using System.Net.Mail;
using System.Threading;

[StatelessWorker]
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
        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[OTP CODE]: {Email}, code: {OtpCode}", email, otpCode);
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

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await Client.SendMailAsync(new MailMessage(smtpOptions.Value.User, email, $"Your Argon verification code", form)
            {
                IsBodyHtml = true
            }, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "failed send otp code to '{email}'", email);
        }
    }

    public async Task SendResetCodeAsync(string email, string otpCode, TimeSpan validity)
    {
        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[OTP RESET CODE]: {Email}, code: {OtpCode}", email, otpCode);
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
            await Client.SendMailAsync(new MailMessage(smtpOptions.Value.User, email, $"Your Argon reset password code", form)
            {
                IsBodyHtml = true
            }, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "failed send reset code to '{email}'", email);
        }
    }

    public async Task SendNotificationResetPasswordAsync(string email)
    {
        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[NOTIFICATION ABOUT RESET PASS]: {Email}", email);
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var form = formStorage.Render("pass_changed", new Dictionary<string, string>());

            await Client.SendMailAsync(new MailMessage(smtpOptions.Value.User, email, $"Your Argon password changed", form)
            {
                IsBodyHtml = true
            }, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "failed send notification to '{email}'", email);
        }
    }
}