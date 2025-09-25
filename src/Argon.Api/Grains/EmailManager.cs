namespace Argon.Grains;

using System.Globalization;
using System.Net.Mail;
using System.Threading;
using DnsClient;
using DnsClient.Protocol;
using Features.Template;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Orleans.Concurrency;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

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

    private async Task SendAsync(string email, MimeMessage message, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateEMailDestination(email, cancellationToken);

        if (!validation.CanSendEmail)
        {
            logger.LogError("Failed send email to {email}, validation failed, {reason}", email, validation.FailureReason);
            return;
        }


        using var client = new SmtpClient();
        message.MessageId = $"{message.MessageId.Split('@').First()}@argon.gl";
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
        return SendAsync(email, msg);
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
            var       msg = CreateMessage(email, "Your Argon verification code", form);
            await SendAsync(email, msg, cts.Token);
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
}