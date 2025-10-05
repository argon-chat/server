namespace Argon.Features.Integrations.Phones.Prelude;

using Argon.Features.Integrations.Phones.Telegram;
using Flurl.Http;

public class PreludeGateway(ILogger<PreludeGateway> logger, IOptions<PreludeGatewayOptions> options)
{
    private readonly IFlurlClient client =
        new FlurlClient(options.Value.endpoint).WithOAuthBearerToken(options.Value.token);

    public async Task<PreludeRequestId?> SendVerificationAsync(
        string phoneNumber,
        string userIp,
        string userAgent,
        string appVersion,
        CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["PhoneNumber"] = phoneNumber
        });

        logger.LogInformation("Sending verification request. AppVersion: {AppVersion}, IP: {UserIp}, UserAgent: {UserAgent}",
            appVersion, userIp, userAgent);

        var result = await client.Request("/v2/verification").PostJsonAsync(new
        {
            target = new
            {
                type  = "phone_number",
                value = phoneNumber,
            },
            signals = new
            {
                app_version     = appVersion,
                device_platform = "web",
                ip              = userIp,
                user_agent      = userAgent
            },
            options = new
            {
                code_size         = 6,
                preferred_channel = "sms",
            }
        }, cancellationToken: ct);

        var resp = await result.GetJsonAsync<PreludeVerificationResp>();

        if (resp.status == ValidateVerificationRequestStatus.success)
        {
            logger.LogInformation("Verification request created successfully. RequestId: {RequestId}, Method: {Method}",
                resp.request_id, resp.method);
            return new PreludeRequestId(resp.request_id);
        }

        logger.LogWarning("Verification request failed. Status: {Status}, Reason: {Reason}",
            resp.status, resp.reason);

        return null;
    }
    public async Task<bool> CheckVerificationAsync(string phoneNumber, string code, CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["PhoneNumber"] = phoneNumber
        });

        logger.LogInformation("Checking verification code {Code}", code);

        var result = await client.Request("/v2/verification").PostJsonAsync(new
        {
            target = new
            {
                type  = "phone_number",
                value = phoneNumber,
            },
            code
        }, cancellationToken: ct);

        var resp = await result.GetJsonAsync<PreludeCheckVerificationResp>();

        if (resp.status == ValidateVerificationRequestStatus.success)
        {
            using var reqScope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["RequestId"] = resp.request_id
            });

            logger.LogInformation("Verification succeeded for RequestId {RequestId}", resp.request_id);
            return true;
        }

        logger.LogWarning("Verification failed. Status: {Status}, RequestId: {RequestId}", resp.status, resp.request_id);
        return false;
    }

    private enum ValidateVerificationRequestStatus
    {
        success,
        failure,
        expired_or_not_found
    }

    private enum VerificationCreateRequestStatus
    {
        success,
        retry,
        blocked
    }

    private record PreludeVerificationResp(string id, ValidateVerificationRequestStatus status, string method, string request_id, string reason);
    private record PreludeCheckVerificationResp(string id, ValidateVerificationRequestStatus status, string request_id);
}

public readonly record struct PreludeRequestId(string request_id);