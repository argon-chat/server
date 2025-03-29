namespace Argon.Api.Features.Orleans.Consul;

using global::Consul;
using global::Orleans.Messaging;

public class ConsulGatewayListProvider(
    IConsulClient client,
    ILogger<IGatewayListProvider> logger,
    [FromKeyedServices("dc")] string dc) : IGatewayListProvider
{
    public Task InitializeGatewayListProvider()
        => Task.CompletedTask;

    public async Task<IList<Uri>> GetGateways()
    {
        try
        {
            var queryOptions = new QueryOptions
            {
                Datacenter = dc,
            };

            var services = await client.Health.Service(
                IArgonUnitMembership.ArgonServiceName, 
                IArgonUnitMembership.GatewayUnit, 
                true,
                queryOptions);

            if (services.StatusCode != HttpStatusCode.OK)
                throw new Exception($"Cannot listen online gateways, httpStatus: {services.StatusCode}");

            var gateways = services
               .Response
               //.Where(x => x.Node.Datacenter.Equals(dc)) // TODO filter with datacenter
               .Select(s => createSiloAddress(s).ToGatewayUri())
               .ToList();

            foreach (var uri in gateways)
                logger.LogDebug("Gateway '{gatewayUri}' found for '{dc}'.", uri, dc);
            return gateways;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "failed list gateways, maybe consul is unavailable");
            return new List<Uri>();
        }
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