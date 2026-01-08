namespace Argon.Features.Integrations.Phones.Prelude;

using Flurl.Http;

public class PreludePhoneChannel : IPhoneChannel
{
    private readonly IFlurlClient _client;
    private readonly ILogger<PreludePhoneChannel> _logger;
    private readonly PreludeChannelOptions _options;

    public PreludePhoneChannel(
        ILogger<PreludePhoneChannel> logger,
        IOptions<PhoneVerificationOptions> options)
    {
        _logger = logger;
        _options = options.Value.Prelude;

        _client = new FlurlClient(_options.Endpoint)
            .WithOAuthBearerToken(_options.Token);
    }

    public PhoneChannelKind Kind => PhoneChannelKind.Prelude;
    public bool IsEnabled => _options.Enabled && !string.IsNullOrEmpty(_options.Token);

    public Task<bool> CanSendAsync(string phoneNumber, CancellationToken ct = default)
        => Task.FromResult(IsEnabled);

    public async Task<PhoneSendResult> SendCodeAsync(PhoneSendRequest request, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return new PhoneSendResult(false, ErrorReason: "Prelude channel is disabled");

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["PhoneNumber"] = request.PhoneNumber,
            ["Channel"] = Kind
        });

        try
        {
            _logger.LogInformation("Sending Prelude verification. IP: {UserIp}", request.UserIp);

            var result = await _client.Request("/v2/verification").PostJsonAsync(new
            {
                target = new
                {
                    type = "phone_number",
                    value = request.PhoneNumber,
                },
                signals = new
                {
                    app_version = request.AppVersion,
                    device_platform = "web",
                    ip = request.UserIp,
                    user_agent = request.UserAgent
                },
                options = new
                {
                    code_size = request.CodeLength,
                    preferred_channel = "sms",
                }
            }, cancellationToken: ct);

            var resp = await result.GetJsonAsync<PreludeVerificationResp>();

            if (resp.status == PreludeStatus.success)
            {
                _logger.LogInformation("Prelude verification sent. RequestId: {RequestId}, Method: {Method}",
                    resp.request_id, resp.method);

                return new PhoneSendResult(
                    Success: true,
                    RequestId: resp.request_id,
                    UsedChannel: Kind);
            }

            _logger.LogWarning("Prelude send failed. Status: {Status}, Reason: {Reason}",
                resp.status, resp.reason);

            return new PhoneSendResult(false, ErrorReason: resp.reason ?? resp.status.ToString());
        }
        catch (FlurlHttpException ex)
        {
            _logger.LogWarning(ex, "Prelude send verification failed");
            return new PhoneSendResult(false, ErrorReason: ex.Message);
        }
    }

    public async Task<PhoneVerifyResult> VerifyCodeAsync(PhoneVerifyRequest request, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return new PhoneVerifyResult(PhoneVerifyStatus.Error);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["PhoneNumber"] = request.PhoneNumber,
            ["Channel"] = Kind
        });

        try
        {
            _logger.LogInformation("Checking Prelude verification");

            var result = await _client.Request("/v2/verification").PostJsonAsync(new
            {
                target = new
                {
                    type = "phone_number",
                    value = request.PhoneNumber,
                },
                code = request.Code
            }, cancellationToken: ct);

            var resp = await result.GetJsonAsync<PreludeCheckResp>();

            var status = resp.status switch
            {
                PreludeStatus.success => PhoneVerifyStatus.Verified,
                PreludeStatus.failure => PhoneVerifyStatus.InvalidCode,
                PreludeStatus.expired_or_not_found => PhoneVerifyStatus.Expired,
                _ => PhoneVerifyStatus.Error
            };

            _logger.LogInformation("Prelude verification result: {Status}", status);
            return new PhoneVerifyResult(status);
        }
        catch (FlurlHttpException ex)
        {
            _logger.LogWarning(ex, "Prelude verify code failed");
            return new PhoneVerifyResult(PhoneVerifyStatus.Error);
        }
    }

    #region Response DTOs

    private enum PreludeStatus
    {
        success,
        failure,
        expired_or_not_found,
        retry,
        blocked
    }

    private record PreludeVerificationResp(
        string id,
        PreludeStatus status,
        string method,
        string request_id,
        string? reason);

    private record PreludeCheckResp(
        string id,
        PreludeStatus status,
        string request_id);

    #endregion
}