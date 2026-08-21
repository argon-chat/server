namespace Argon.Features.GeoIP;

using Flurl.Http;
using Microsoft.Extensions.Logging;

public static class GeoIpFeature
{
    public static WebApplicationBuilder AddGeoIpSupport(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<GeoIpOptions>(builder.Configuration.GetSection("GeoIp"));
        builder.Services.AddSingleton<IGeoIp, InternalGeoIp>();
        return builder;
    }
}

public interface IGeoIp
{
    Task<GeoResponse> GetAsync(string ip, CancellationToken ct = default);
}

public class GeoIpOptions
{
    public string Address { get; set; }
}

public class InternalGeoIp(IOptions<GeoIpOptions> options, ILogger<IGeoIp> logger) : IGeoIp
{
    public async Task<GeoResponse> GetAsync(string ip, CancellationToken ct = default)
    {
        try
        {
            var response = await $"{options.Value.Address}/geo/{ip}"
               .WithTimeout(TimeSpan.FromSeconds(5))
               .GetJsonAsync<GeoResponse>(cancellationToken: ct);

            if (response != null)
                return response;
            logger.LogWarning("Geo API returned null response for IP: {IP}", ip);
            return new GeoResponse { IP = ip, Error = "No data available" };

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while fetching geo data for IP: {IP}", ip);
            throw new GeoIpRequestException($"Unexpected error while fetching geo data for IP: {ip}");
        }
    }
}

public class GeoIpRequestException(string msg) : Exception(msg);