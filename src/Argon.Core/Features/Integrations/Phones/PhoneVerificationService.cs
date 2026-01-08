namespace Argon.Features.Integrations.Phones;

using System.Collections.Concurrent;

/// <summary>
/// Phone verification service with fallback logic.
/// Priority: Telegram (free) -> Prelude/Twilio (paid fallback)
/// </summary>
public class PhoneVerificationService : IPhoneProvider
{
    private readonly ILogger<PhoneVerificationService> _logger;
    private readonly PhoneVerificationOptions _options;
    private readonly NullPhoneChannel _nullChannel;
    private readonly IPhoneChannel? _telegramChannel;
    private readonly IPhoneChannel? _preludeChannel;
    private readonly IPhoneChannel? _twilioChannel;

    // Track which channel was used for each phone number (for verification)
    private readonly ConcurrentDictionary<string, (PhoneChannelKind Channel, string? RequestId)> _pendingVerifications = new();

    public PhoneVerificationService(
        ILogger<PhoneVerificationService> logger,
        IOptions<PhoneVerificationOptions> options,
        NullPhoneChannel nullChannel,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _options = options.Value;
        _nullChannel = nullChannel;

        // Only resolve channels that are enabled
        if (_options.Telegram.Enabled)
            _telegramChannel = serviceProvider.GetService<Telegram.TelegramPhoneChannel>();

        if (_options.Prelude.Enabled)
            _preludeChannel = serviceProvider.GetService<Prelude.PreludePhoneChannel>();

        if (_options.Twilio.Enabled)
            _twilioChannel = serviceProvider.GetService<Twilio.TwilioPhoneChannel>();
    }

    public async Task SendCode(string phone, string ip, string ua, string appVersion)
    {
        var request = new PhoneSendRequest(phone, ip, ua, appVersion);

        // If phone verification is disabled, use null channel
        if (!_options.Enabled)
        {
            var nullResult = await _nullChannel.SendCodeAsync(request);
            if (nullResult.Success)
                _pendingVerifications[phone] = (PhoneChannelKind.Null, nullResult.RequestId);
            return;
        }

        // Try Telegram first (free)
        if (_telegramChannel is { IsEnabled: true })
        {
            var canSend = await _telegramChannel.CanSendAsync(phone);
            if (canSend)
            {
                var result = await _telegramChannel.SendCodeAsync(request);
                if (result.Success)
                {
                    _pendingVerifications[phone] = (PhoneChannelKind.Telegram, result.RequestId);
                    _logger.LogInformation("Phone verification sent via Telegram to {Phone}", phone);
                    return;
                }

                _logger.LogWarning("Telegram send failed for {Phone}: {Error}, trying fallback",
                    phone, result.ErrorReason);
            }
            else
            {
                _logger.LogDebug("Telegram cannot send to {Phone} (user not registered), trying fallback", phone);
            }
        }

        // Fallback to Prelude
        if (_preludeChannel is { IsEnabled: true })
        {
            var result = await _preludeChannel.SendCodeAsync(request);
            if (result.Success)
            {
                _pendingVerifications[phone] = (PhoneChannelKind.Prelude, result.RequestId);
                _logger.LogInformation("Phone verification sent via Prelude to {Phone}", phone);
                return;
            }

            _logger.LogWarning("Prelude send failed for {Phone}: {Error}, trying Twilio", phone, result.ErrorReason);
        }

        // Fallback to Twilio
        if (_twilioChannel is { IsEnabled: true })
        {
            var result = await _twilioChannel.SendCodeAsync(request);
            if (result.Success)
            {
                _pendingVerifications[phone] = (PhoneChannelKind.Twilio, result.RequestId);
                _logger.LogInformation("Phone verification sent via Twilio to {Phone}", phone);
                return;
            }

            _logger.LogWarning("Twilio send failed for {Phone}: {Error}", phone, result.ErrorReason);
        }

        // Last resort: null channel (for development)
        _logger.LogWarning("All phone channels failed for {Phone}, using null channel", phone);
        var fallbackResult = await _nullChannel.SendCodeAsync(request);
        if (fallbackResult.Success)
            _pendingVerifications[phone] = (PhoneChannelKind.Null, fallbackResult.RequestId);
    }

    public async Task<VerifyResult> VerifyCode(string phone, string requestId, string otpCode)
    {
        // Find which channel was used
        if (!_pendingVerifications.TryGetValue(phone, out var pending))
        {
            _logger.LogWarning("No pending verification found for {Phone}", phone);
            return new VerifyResult(VerifyStatus.WrongCode, 0);
        }

        var channel = GetChannel(pending.Channel);
        if (channel is null)
        {
            _logger.LogError("Channel {Channel} not available for verification", pending.Channel);
            return new VerifyResult(VerifyStatus.WrongCode, 0);
        }

        var request = new PhoneVerifyRequest(phone, pending.RequestId ?? requestId, otpCode);
        var result = await channel.VerifyCodeAsync(request);

        var verifyResult = result.Status switch
        {
            PhoneVerifyStatus.Verified => VerifyStatus.Verified,
            PhoneVerifyStatus.TooManyAttempts => VerifyStatus.TooManyAttempts,
            _ => VerifyStatus.WrongCode
        };

        if (result.Status == PhoneVerifyStatus.Verified)
            _pendingVerifications.TryRemove(phone, out _);

        return new VerifyResult(verifyResult, result.RemainingAttempts, result.RetryAfter);
    }

    private IPhoneChannel? GetChannel(PhoneChannelKind kind) => kind switch
    {
        PhoneChannelKind.Telegram => _telegramChannel,
        PhoneChannelKind.Prelude => _preludeChannel,
        PhoneChannelKind.Twilio => _twilioChannel,
        PhoneChannelKind.Null => _nullChannel,
        _ => null
    };
}
