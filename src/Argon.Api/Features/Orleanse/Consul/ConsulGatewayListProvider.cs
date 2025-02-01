namespace Argon.Api.Features.Orleans.Consul;

using global::Consul;
using global::Orleans.Messaging;

public class ConsulGatewayListProvider(IConsulClient client, ILogger<IGatewayListProvider> logger) : IGatewayListProvider
{
    public Task InitializeGatewayListProvider()
        => Task.CompletedTask;

    public async Task<IList<Uri>> GetGateways()
    {
        var services = await client.Health.Service("Silo", "silo", true);
        if (services.StatusCode != HttpStatusCode.OK)
            throw new Exception($"Cannot listen online gateways");

        var gateways = services
           .Response
           .Select(s => new Uri($"gwy.tcp://{s.Service.Address}:{s.Service.Port}"))
           .ToList();

        logger.LogInformation("Found {Count} gateways in Consul", gateways.Count);
        return gateways;
    }

    public TimeSpan MaxStaleness => TimeSpan.FromSeconds(30);
    public bool     IsUpdatable  => true;
}