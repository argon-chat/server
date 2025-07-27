namespace Argon.Sfu.Services;

using Flurl.Http;

public class TwirpClient([FromKeyedServices(IArgonSelectiveForwardingUnit.CHANNEL_KEY)] IFlurlClient client)
{
    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string service,
        string method,
        TRequest request,
        string? bearerToken = null,
        CancellationToken ct = default)
    {
        var url = $"/twirp/{service}/{method}";

        var req = client.Request(url)
           .WithTimeout(15);

        if (!string.IsNullOrEmpty(bearerToken))
            req = req.WithOAuthBearerToken(bearerToken);

        var response = await req
           .AllowAnyHttpStatus()
           .PostJsonAsync(request, cancellationToken: ct);

        if (response.ResponseMessage.IsSuccessStatusCode) return await response.GetJsonAsync<TResponse>();
        var err = await response.GetStringAsync();
        throw new Exception($"Twirp error: {response.StatusCode}\n{err}");
    }
}
