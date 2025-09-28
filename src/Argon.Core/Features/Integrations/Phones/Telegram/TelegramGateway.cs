namespace Argon.Features.Integrations.Phones.Telegram;

using Flurl.Http;
using Microsoft.Extensions.Logging;

public class TelegramGateway(ILogger<TelegramGateway> logger, IOptions<TelegramGatewayOptions> options)
{
    private readonly IFlurlClient client = 
        new FlurlClient(options.Value.endpoint).WithOAuthBearerToken(options.Value.token);

    public async Task<bool> CheckSendAbilityAsync(string phoneNumber, CancellationToken ct = default)
    {
        if (!options.Value.isEnabled)
            return false;

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["PhoneNumber"] = phoneNumber
        });

        logger.LogInformation("Checking send ability");

        var result = await client.Request("/checkSendAbility").PostJsonAsync(new
        {
            phone_number = phoneNumber
        }, cancellationToken: ct);

        var resp = await result.GetJsonAsync<TelegramGatewayResponse<CheckSendAbilityResp>>();

        if (!resp.ok)
        {
            logger.LogWarning("Send ability check failed. Error: {Error}", resp.error);
            return false;
        }

        if (resp.result.remaining_balance == 0)
        {
            logger.LogCritical("Remaining balance is zero");
            return false;
        }

        if (resp.result.remaining_balance - resp.result.request_cost < 0)
        {
            logger.LogCritical("Insufficient balance. Balance: {Balance}, Cost: {Cost}",
                resp.result.remaining_balance, resp.result.request_cost);
            return false;
        }

        logger.LogInformation("Send ability confirmed. Balance: {Balance}, Cost: {Cost}",
            resp.result.remaining_balance, resp.result.request_cost);

        return true;
    }

    public async Task<GatewayRequestId?> SendVerificationMessage(string phoneNumber, int codeLen = 6, CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["PhoneNumber"] = phoneNumber
        });

        logger.LogInformation("Sending verification message with code length {CodeLength}", codeLen);

        var result = await client.Request("/sendVerificationMessage").PostJsonAsync(new
        {
            phone_number = phoneNumber,
            code_length = codeLen,
            ttl = 600
        }, cancellationToken: ct);

        var resp = await result.GetJsonAsync<TelegramGatewayResponse<SendVerificationMessageResp>>();

        if (!resp.ok)
        {
            logger.LogWarning("Failed to send verification message. Error: {Error}", resp.error);
            return null;
        }

        logger.LogInformation(
            "Verification message sent. RequestId: {RequestId}, Cost: {Cost}, Balance: {Balance}",
            resp.result.request_id, resp.result.request_cost, resp.result.remaining_balance
        );

        return new GatewayRequestId(resp.result.request_id);
    }

    public async Task<bool> CheckVerificationStatus(GatewayRequestId requestId, string code, CancellationToken ct = default)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["RequestId"] = requestId.request_id
        });

        logger.LogInformation("Checking verification status with code {Code}", code);

        var result = await client.Request("/checkVerificationStatus").PostJsonAsync(new
        {
            request_id = requestId.request_id,
            code = code
        }, cancellationToken: ct);

        var resp = await result.GetJsonAsync<TelegramGatewayResponse<CheckVerificationStatusResp>>();

        if (!resp.ok)
        {
            logger.LogWarning("Verification status check failed. Error: {Error}", resp.error);
            return false;
        }

        var isValid = resp.result.verification_status.status == VerificationStatusKind.code_valid;

        logger.LogInformation("Verification status: {Status}", resp.result.verification_status.status);

        return isValid;
    }


    private enum TgGatewayError
    {
        PAYLOAD_INVALID,
        PHONE_NUMBER_NOT_AVAILABLE
    }

    private record TelegramGatewayResponse<T>(bool ok, T result, TgGatewayError? error);

    private record CheckSendAbilityResp(string request_id, string phone_number, decimal request_cost, decimal remaining_balance);

    private record SendVerificationMessageResp(
        string request_id,
        string phone_number,
        decimal request_cost,
        decimal remaining_balance,
        DeliveryStatusEntity delivery_status
    );

    private enum DeliveryStatusKind
    {
        sent,
        delivered,
        read,
        expired,
        revoked
    }

    private record DeliveryStatusEntity(DeliveryStatusKind status, long updated_at);

    private enum VerificationStatusKind
    {
        code_valid,
        code_invalid,
        code_max_attempts_exceeded,
        expired
    }

    private record VerificationStatusEntity(VerificationStatusKind status, long updated_at, string code_entered);

    private record CheckVerificationStatusResp(
        string request_id,
        string phone_number,
        decimal request_cost,
        decimal remaining_balance,
        DeliveryStatusEntity delivery_status,
        VerificationStatusEntity verification_status
    );
}

public readonly record struct GatewayRequestId(string request_id);
