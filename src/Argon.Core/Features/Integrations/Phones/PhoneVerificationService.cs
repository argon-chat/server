namespace Argon.Features.Integrations.Phones;

using System.Collections.Concurrent;
using Prelude;
using Twilio;

/// <summary>
/// Phone verification service with fallback logic.
/// Priority: Telegram (free) -> Prelude/Twilio (paid fallback)
/// </summary>
public class PhoneVerificationService(
    ILogger<PhoneVerificationService> logger,
    IOptions<PhoneVerificationOptions> options,
    NullPhoneChannel nullChannel,
    IServiceProvider serviceProvider)
    : IPhoneProvider
{
    private IPhoneChannel? telegramChannel
    {
        get
        {
            if (options.Value.Telegram.Enabled)
                return serviceProvider.GetService<Telegram.TelegramPhoneChannel>();
            return null;
        }
    }

    private IPhoneChannel? preludeChannel
    {
        get
        {
            if (options.Value.Prelude.Enabled)
                return serviceProvider.GetService<PreludePhoneChannel>();
            return null;
        }
    }
    private IPhoneChannel? twilioChannel
    {
        get
        {
            if (options.Value.Twilio.Enabled)
                return serviceProvider.GetService<TwilioPhoneChannel>();
            return null;
        }
    }

    // Track which channel was used for each phone number (for verification)
    private readonly ConcurrentDictionary<string, (PhoneChannelKind Channel, string? RequestId)> _pendingVerifications = new();

    public async Task SendCode(string phone, string ip, string ua, string appVersion)
    {
        var request = new PhoneSendRequest(phone, ip, ua, appVersion);

        // If phone verification is disabled, use null channel
        if (!options.Value.Enabled)
        {
            var nullResult = await nullChannel.SendCodeAsync(request);
            if (nullResult.Success)
                _pendingVerifications[phone] = (PhoneChannelKind.Null, nullResult.RequestId);
            return;
        }

        // Try Telegram first (free)
        if (telegramChannel is { IsEnabled: true })
        {
            var canSend = await telegramChannel.CanSendAsync(phone);
            if (canSend)
            {
                var result = await telegramChannel.SendCodeAsync(request);
                if (result.Success)
                {
                    _pendingVerifications[phone] = (PhoneChannelKind.Telegram, result.RequestId);
                    logger.LogInformation("Phone verification sent via Telegram to {Phone}", phone);
                    return;
                }

                logger.LogWarning("Telegram send failed for {Phone}: {Error}, trying fallback",
                    phone, result.ErrorReason);
            }
            else
            {
                logger.LogDebug("Telegram cannot send to {Phone} (user not registered), trying fallback", phone);
            }
        }

        // Fallback to Prelude
        if (preludeChannel is { IsEnabled: true })
        {
            var result = await preludeChannel.SendCodeAsync(request);
            if (result.Success)
            {
                _pendingVerifications[phone] = (PhoneChannelKind.Prelude, result.RequestId);
                logger.LogInformation("Phone verification sent via Prelude to {Phone}", phone);
                return;
            }

            logger.LogWarning("Prelude send failed for {Phone}: {Error}, trying Twilio", phone, result.ErrorReason);
        }

        // Fallback to Twilio
        if (twilioChannel is { IsEnabled: true })
        {
            var result = await twilioChannel.SendCodeAsync(request);
            if (result.Success)
            {
                _pendingVerifications[phone] = (PhoneChannelKind.Twilio, result.RequestId);
                logger.LogInformation("Phone verification sent via Twilio to {Phone}", phone);
                return;
            }

            logger.LogWarning("Twilio send failed for {Phone}: {Error}", phone, result.ErrorReason);
        }

        // Last resort: null channel (for development)
        logger.LogWarning("All phone channels failed for {Phone}, using null channel", phone);
        var fallbackResult = await nullChannel.SendCodeAsync(request);
        if (fallbackResult.Success)
            _pendingVerifications[phone] = (PhoneChannelKind.Null, fallbackResult.RequestId);
    }

    public async Task<VerifyResult> VerifyCode(string phone, string requestId, string otpCode)
    {
        // Find which channel was used
        if (!_pendingVerifications.TryGetValue(phone, out var pending))
        {
            logger.LogWarning("No pending verification found for {Phone}", phone);
            return new VerifyResult(VerifyStatus.WrongCode, 0);
        }

        var channel = GetChannel(pending.Channel);
        if (channel is null)
        {
            logger.LogError("Channel {Channel} not available for verification", pending.Channel);
            return new VerifyResult(VerifyStatus.WrongCode, 0);
        }

        var request = new PhoneVerifyRequest(phone, pending.RequestId ?? requestId, otpCode);
        var result  = await channel.VerifyCodeAsync(request);

        var verifyResult = result.Status switch
        {
            PhoneVerifyStatus.Verified        => VerifyStatus.Verified,
            PhoneVerifyStatus.TooManyAttempts => VerifyStatus.TooManyAttempts,
            _                                 => VerifyStatus.WrongCode
        };

        if (result.Status == PhoneVerifyStatus.Verified)
            _pendingVerifications.TryRemove(phone, out _);

        return new VerifyResult(verifyResult, result.RemainingAttempts, result.RetryAfter);
    }

    private IPhoneChannel? GetChannel(PhoneChannelKind kind) => kind switch
    {
        PhoneChannelKind.Telegram => telegramChannel,
        PhoneChannelKind.Prelude  => preludeChannel,
        PhoneChannelKind.Twilio   => twilioChannel,
        PhoneChannelKind.Null     => nullChannel,
        _                         => null
    };
}