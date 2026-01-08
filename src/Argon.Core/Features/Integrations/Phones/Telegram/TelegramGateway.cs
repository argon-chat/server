namespace Argon.Features.Integrations.Phones.Telegram;

using Flurl.Http;
using Flurl.Http.Newtonsoft;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

public class TelegramPhoneChannel : IPhoneChannel
{
    private readonly IFlurlClient _client;
    private readonly ILogger<TelegramPhoneChannel> _logger;
    private readonly TelegramChannelOptions _options;

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Converters = { new StringEnumConverter() },
        NullValueHandling = NullValueHandling.Ignore
    };

    private const int MaxFloodWaitRetries = 3;
    private const int MaxFloodWaitSeconds = 30;

    public TelegramPhoneChannel(
        ILogger<TelegramPhoneChannel> logger,
        IOptions<PhoneVerificationOptions> options)
    {
        _logger = logger;
        _options = options.Value.Telegram;

        _client = new FlurlClient(_options.Endpoint)
            .WithOAuthBearerToken(_options.Token)
            .WithSettings(s => s.JsonSerializer = new NewtonsoftJsonSerializer(JsonSettings));
    }

    public PhoneChannelKind Kind => PhoneChannelKind.Telegram;
    public bool IsEnabled => _options.Enabled && !string.IsNullOrEmpty(_options.Token);

    public async Task<bool> CanSendAsync(string phoneNumber, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return false;

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["PhoneNumber"] = phoneNumber,
            ["Channel"] = Kind
        });

        try
        {
            _logger.LogDebug("Checking Telegram send ability");

            var result = await _client.Request("/checkSendAbility").PostJsonAsync(new
            {
                phone_number = phoneNumber
            }, cancellationToken: ct);

            var resp = await result.GetJsonAsync<TelegramGatewayResponse<CheckSendAbilityResp>>();

            if (!resp.ok || resp.result is null)
            {
                _logger.LogDebug("Telegram send ability check failed: {Error}", resp.error);
                return false;
            }

            if (resp.result.remaining_balance <= 0)
            {
                _logger.LogCritical("Telegram Gateway: remaining balance is zero");
                return false;
            }

            if (resp.result.remaining_balance - resp.result.request_cost < 0)
            {
                _logger.LogCritical("Telegram Gateway: insufficient balance. Balance: {Balance}, Cost: {Cost}",
                    resp.result.remaining_balance, resp.result.request_cost);
                return false;
            }

            _logger.LogDebug("Telegram can send. Balance: {Balance}", resp.result.remaining_balance);
            return true;
        }
        catch (FlurlHttpException ex)
        {
            _logger.LogWarning(ex, "Telegram checkSendAbility failed");
            return false;
        }
    }

    public async Task<PhoneSendResult> SendCodeAsync(PhoneSendRequest request, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return new PhoneSendResult(false, ErrorReason: "Telegram channel is disabled");

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["PhoneNumber"] = request.PhoneNumber,
            ["Channel"] = Kind
        });

        return await SendCodeWithRetryAsync(request, 0, ct);
    }

    private async Task<PhoneSendResult> SendCodeWithRetryAsync(PhoneSendRequest request, int retryCount, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Sending Telegram verification message (attempt {Attempt})", retryCount + 1);

            var result = await _client.Request("/sendVerificationMessage").PostJsonAsync(new
            {
                phone_number = request.PhoneNumber,
                code_length = request.CodeLength,
                ttl = 600
            }, cancellationToken: ct);

            var resp = await result.GetJsonAsync<TelegramGatewayResponse<SendVerificationMessageResp>>();

            if (!resp.ok || resp.result is null)
            {
                // Check for FLOOD_WAIT error
                if (resp.error is not null && TryParseFloodWait(resp.error, out var waitSeconds))
                {
                    if (retryCount < MaxFloodWaitRetries && waitSeconds <= MaxFloodWaitSeconds)
                    {
                        _logger.LogWarning("Telegram FLOOD_WAIT_{Seconds}, waiting and retrying...", waitSeconds);
                        await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
                        return await SendCodeWithRetryAsync(request, retryCount + 1, ct);
                    }

                    _logger.LogWarning("Telegram FLOOD_WAIT_{Seconds} exceeded max retries or wait time", waitSeconds);
                }

                _logger.LogWarning("Telegram send failed: {Error}", resp.error);
                return new PhoneSendResult(false, ErrorReason: resp.error);
            }

            _logger.LogInformation(
                "Telegram verification sent. RequestId: {RequestId}, Cost: {Cost}",
                resp.result.request_id, resp.result.request_cost);

            return new PhoneSendResult(
                Success: true,
                RequestId: resp.result.request_id,
                UsedChannel: Kind);
        }
        catch (FlurlHttpException ex)
        {
            _logger.LogWarning(ex, "Telegram sendVerificationMessage failed");
            return new PhoneSendResult(false, ErrorReason: ex.Message);
        }
    }

    private static bool TryParseFloodWait(string error, out int seconds)
    {
        seconds = 0;
        if (!error.StartsWith("FLOOD_WAIT_", StringComparison.OrdinalIgnoreCase))
            return false;

        var secondsPart = error["FLOOD_WAIT_".Length..];
        return int.TryParse(secondsPart, out seconds) && seconds > 0;
    }

    public async Task<PhoneVerifyResult> VerifyCodeAsync(PhoneVerifyRequest request, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return new PhoneVerifyResult(PhoneVerifyStatus.Error);

        if (string.IsNullOrEmpty(request.RequestId))
            return new PhoneVerifyResult(PhoneVerifyStatus.NotFound);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestId"] = request.RequestId,
            ["Channel"] = Kind
        });

        try
        {
            _logger.LogInformation("Checking Telegram verification status");

            var result = await _client.Request("/checkVerificationStatus").PostJsonAsync(new
            {
                request_id = request.RequestId,
                code = request.Code
            }, cancellationToken: ct);

            var resp = await result.GetJsonAsync<TelegramGatewayResponse<CheckVerificationStatusResp>>();

            if (!resp.ok || resp.result is null)
            {
                _logger.LogWarning("Telegram verification check failed: {Error}", resp.error);
                return new PhoneVerifyResult(PhoneVerifyStatus.Error);
            }

            var status = resp.result.verification_status.status switch
            {
                VerificationStatusKind.code_valid => PhoneVerifyStatus.Verified,
                VerificationStatusKind.code_invalid => PhoneVerifyStatus.InvalidCode,
                VerificationStatusKind.code_max_attempts_exceeded => PhoneVerifyStatus.TooManyAttempts,
                VerificationStatusKind.expired => PhoneVerifyStatus.Expired,
                _ => PhoneVerifyStatus.Error
            };

            _logger.LogInformation("Telegram verification result: {Status}", status);
            return new PhoneVerifyResult(status);
        }
        catch (FlurlHttpException ex)
        {
            _logger.LogWarning(ex, "Telegram checkVerificationStatus failed");
            return new PhoneVerifyResult(PhoneVerifyStatus.Error);
        }
    }

    #region Response DTOs

    private record TelegramGatewayResponse<T>(bool ok, T? result, string? error);

    private record CheckSendAbilityResp(
        string request_id,
        string phone_number,
        decimal request_cost,
        decimal remaining_balance);

    private record SendVerificationMessageResp(
        string request_id,
        string phone_number,
        decimal request_cost,
        decimal remaining_balance,
        DeliveryStatusEntity delivery_status);

    private enum DeliveryStatusKind { sent, delivered, read, expired, revoked }
    private record DeliveryStatusEntity(DeliveryStatusKind status, long updated_at);

    private enum VerificationStatusKind { code_valid, code_invalid, code_max_attempts_exceeded, expired }
    private record VerificationStatusEntity(VerificationStatusKind status, long updated_at, string? code_entered);

    private record CheckVerificationStatusResp(
        string request_id,
        string phone_number,
        decimal request_cost,
        decimal remaining_balance,
        DeliveryStatusEntity delivery_status,
        VerificationStatusEntity verification_status);

    #endregion
}
