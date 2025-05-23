namespace Argon.Grains;

using Features.Template;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Orleans.Concurrency;
using System.Threading;

[StatelessWorker]
public class EmailManager(IOptions<SmtpConfig> smtpOptions, ILogger<EmailManager> logger, EMailFormStorage formStorage) : Grain, IEmailManager
{
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

    private async Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
    {
        using var client = new SmtpClient();

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
        var msg = CreateMessage(email, subject, message);
        return SendAsync(msg);
    }

    public async Task SendOtpCodeAsync(string email, string otpCode, TimeSpan validity)
    {
        if (!smtpOptions.Value.Enabled)
        {
            logger.LogWarning("[OTP CODE]: {Email}, code: {OtpCode}", email, otpCode);
            return;
        }

        var form = formStorage.Render("otp", new Dictionary<string, string>
        {
            { "otp", otpCode },
            { "validity", $"{(int)Math.Floor(validity.TotalMinutes):D}" }
        });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var msg = CreateMessage(email, "Your Argon verification code", form);
            await SendAsync(msg, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send OTP code to '{email}'", email);
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
            { "reset_code", otpCode }
        });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var msg = CreateMessage(email, "Your Argon reset password code", form);
            await SendAsync(msg, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send reset code to '{email}'", email);
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
            var msg = CreateMessage(email, "Your Argon password changed", form);
            await SendAsync(msg, cts.Token);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to send notification to '{email}'", email);
        }
    }
}
