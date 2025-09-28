namespace Argon.Features.Integrations.Phones.Prelude;

using Flurl.Http;

public record PreludeGatewayOptions(string endpoint, string token);

public class PreludeGateway(ILogger<PreludeGateway> logger, IOptions<PreludeGatewayOptions> options)
{
    private readonly IFlurlClient client =
        new FlurlClient(options.Value.endpoint).WithOAuthBearerToken(options.Value.token);

    public async Task<PreludeRequestId?> SendVerificationAsync(string phoneNumber, string userIp, string user_agent, string app_version,
        CancellationToken ct = default)
    {
        var result = await client.Request("/v2/verification").PostJsonAsync(new
        {
            target = new
            {
                type  = "phone_number",
                value = phoneNumber,
            },
            signals = new
            {
                app_version,
                device_platform = "web",
                ip              = userIp,
                user_agent
            },
            options = new
            {
                code_size         = 6,
                preferred_channel = "sms",
            }
        }, cancellationToken: ct);

        var resp = await result.GetJsonAsync<PreludeVerificationResp>();

        if (resp.status == "success")
            return new PreludeRequestId(resp.request_id);
        // log
        return null;
    }


    /*request_id*/
    /*{
           "id": "vrf_01k67tthpbem7t6xx8bjb5wdqa",
           "status": "success",
           "method": "message",
           "metadata": {},
           "request_id": "522544ea-ee9a-41da-b978-2d854f319ca2"
       }*/
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

    private record PreludeVerificationResp(string id, string status, string method, string request_id, string reason);
}

public readonly record struct PreludeRequestId(string request_id);