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
           .Select(s => createSiloAddress(s).ToGatewayUri())
           .ToList();

        logger.LogInformation("Found {Count} gateways in Consul", gateways.Count);
        foreach (var uri in gateways)
            logger.LogInformation("Gateway '{gatewayUri}' found.", uri);
        return gateways;
    }


    private SiloAddress createSiloAddress(ServiceEntry entry)
    {
        if (!entry.Service.Meta.TryGetValue("gen", out var genStr))
            throw new InvalidOperationException($"No 'gen' field in ServiceEntry on Consul registered");
        return SiloAddress.New(IPAddress.Parse(entry.Service.Address), entry.Service.Port, int.Parse(genStr));
    }

    public TimeSpan MaxStaleness => TimeSpan.FromSeconds(30);
    public bool     IsUpdatable  => true;
}