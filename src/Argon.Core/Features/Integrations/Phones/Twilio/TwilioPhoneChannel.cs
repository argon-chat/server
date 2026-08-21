namespace Argon.Features.Integrations.Phones.Twilio;

using global::Twilio;
using global::Twilio.Rest.Verify.V2.Service;
using global::Twilio.Exceptions;
using System.Diagnostics;

public class TwilioPhoneChannel : IPhoneChannel
{
    private readonly ILogger<TwilioPhoneChannel> _logger;
    private readonly TwilioChannelOptions _options;
    private readonly bool _initialized;

    public TwilioPhoneChannel(
        ILogger<TwilioPhoneChannel> logger,
        IOptions<PhoneVerificationOptions> options)
    {
        _logger = logger;
        _options = options.Value.Twilio;

        if (!IsEnabled) return;
        TwilioClient.Init(_options.AccountSid, _options.AuthToken);
        _initialized = true;
    }

    public PhoneChannelKind Kind => PhoneChannelKind.Twilio;

    public bool IsEnabled => _options.Enabled
        && !string.IsNullOrEmpty(_options.AccountSid)
        && !string.IsNullOrEmpty(_options.AuthToken)
        && !string.IsNullOrEmpty(_options.VerifyServiceSid);

    public Task<bool> CanSendAsync(string phoneNumber, CancellationToken ct = default)
        => Task.FromResult(IsEnabled && _initialized);

    public async Task<PhoneSendResult> SendCodeAsync(PhoneSendRequest request, CancellationToken ct = default)
    {
        if (!IsEnabled || !_initialized)
        {
            PhoneInstrument.VerificationSent.Add(1,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "failed"));
            return new PhoneSendResult(false, ErrorReason: "Twilio channel is disabled");
        }

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["PhoneNumber"] = request.PhoneNumber,
            ["Channel"] = Kind
        });

        var sw = Stopwatch.StartNew();
        try
        {
            _logger.LogInformation("Sending Twilio verification");

            var verification = await VerificationResource.CreateAsync(
                to: request.PhoneNumber,
                channel: "sms",
                pathServiceSid: _options.VerifyServiceSid
            );

            sw.Stop();

            if (verification.Status is "pending" or "approved")
            {
                PhoneInstrument.VerificationSent.Add(1,
                    new KeyValuePair<string, object?>("channel", "twilio"),
                    new KeyValuePair<string, object?>("status", "success"));
                
                PhoneInstrument.SendDuration.Record(sw.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("channel", "twilio"),
                    new KeyValuePair<string, object?>("status", "success"));

                _logger.LogInformation("Twilio verification sent. SID: {Sid}, Status: {Status}",
                    verification.Sid, verification.Status);

                return new PhoneSendResult(
                    Success: true,
                    RequestId: verification.Sid,
                    UsedChannel: Kind);
            }

            PhoneInstrument.VerificationSent.Add(1,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "failed"));
            
            PhoneInstrument.SendDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "failed"));

            _logger.LogWarning("Twilio send failed. Status: {Status}", verification.Status);
            return new PhoneSendResult(false, ErrorReason: verification.Status);
        }
        catch (ApiException ex)
        {
            sw.Stop();
            
            PhoneInstrument.VerificationSent.Add(1,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "failed"));
            
            PhoneInstrument.SendDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "failed"));

            _logger.LogWarning(ex, "Twilio send verification failed. Code: {Code}, Message: {Message}",
                ex.Code, ex.Message);
            return new PhoneSendResult(false, ErrorReason: $"{ex.Code}: {ex.Message}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            
            PhoneInstrument.VerificationSent.Add(1,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "failed"));
            
            PhoneInstrument.SendDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "failed"));

            _logger.LogWarning(ex, "Twilio send verification failed");
            return new PhoneSendResult(false, ErrorReason: ex.Message);
        }
    }

    public async Task<PhoneVerifyResult> VerifyCodeAsync(PhoneVerifyRequest request, CancellationToken ct = default)
    {
        if (!IsEnabled || !_initialized)
        {
            PhoneInstrument.VerificationChecks.Add(1,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "error"));
            return new PhoneVerifyResult(PhoneVerifyStatus.Error);
        }

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["PhoneNumber"] = request.PhoneNumber,
            ["Channel"] = Kind
        });

        var sw = Stopwatch.StartNew();
        try
        {
            _logger.LogInformation("Checking Twilio verification");

            var verificationCheck = await VerificationCheckResource.CreateAsync(
                to: request.PhoneNumber,
                code: request.Code,
                pathServiceSid: _options.VerifyServiceSid
            );

            sw.Stop();

            var status = verificationCheck.Status switch
            {
                "approved" => PhoneVerifyStatus.Verified,
                "pending" => PhoneVerifyStatus.InvalidCode,
                "canceled" or "expired" => PhoneVerifyStatus.Expired,
                "max_attempts_reached" => PhoneVerifyStatus.TooManyAttempts,
                _ => PhoneVerifyStatus.Error
            };

            var statusTag = status switch
            {
                PhoneVerifyStatus.Verified => "verified",
                PhoneVerifyStatus.InvalidCode => "invalid",
                PhoneVerifyStatus.Expired => "expired",
                PhoneVerifyStatus.TooManyAttempts => "too_many_attempts",
                _ => "error"
            };

            PhoneInstrument.VerificationChecks.Add(1,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", statusTag));
            
            PhoneInstrument.CheckDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("channel", "twilio"));

            _logger.LogInformation("Twilio verification result: {Status}", status);
            return new PhoneVerifyResult(status);
        }
        catch (ApiException ex) when (ex.Code == 20404)
        {
            sw.Stop();
            
            PhoneInstrument.VerificationChecks.Add(1,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "error"));
            
            PhoneInstrument.CheckDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("channel", "twilio"));

            _logger.LogWarning("Twilio verification not found");
            return new PhoneVerifyResult(PhoneVerifyStatus.NotFound);
        }
        catch (ApiException ex)
        {
            sw.Stop();
            
            PhoneInstrument.VerificationChecks.Add(1,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "error"));
            
            PhoneInstrument.CheckDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("channel", "twilio"));

            _logger.LogWarning(ex, "Twilio verify code failed. Code: {Code}, Message: {Message}",
                ex.Code, ex.Message);
            return new PhoneVerifyResult(PhoneVerifyStatus.Error);
        }
        catch (Exception ex)
        {
            sw.Stop();
            
            PhoneInstrument.VerificationChecks.Add(1,
                new KeyValuePair<string, object?>("channel", "twilio"),
                new KeyValuePair<string, object?>("status", "error"));
            
            PhoneInstrument.CheckDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("channel", "twilio"));

            _logger.LogWarning(ex, "Twilio verify code failed");
            return new PhoneVerifyResult(PhoneVerifyStatus.Error);
        }
    }
}
