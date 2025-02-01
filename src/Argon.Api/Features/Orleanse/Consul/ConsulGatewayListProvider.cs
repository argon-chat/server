namespace Argon.Api.Features.Orleans.Consul;

using System.Text.Json;
using global::Consul;
using global::Orleans.Messaging;
using global::Orleans.Runtime.Membership;
using OtpNet;

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
           .Select(s => EjectEntry(s.Service).SiloAddress.ToGatewayUri())
           .ToList();

        logger.LogInformation("Found {Count} gateways in Consul", gateways.Count);
        foreach (var uri in gateways)
            logger.LogInformation("Gateway '{gatewayUri}' found.", uri);
        return gateways;
    }


    private MembershipEntry EjectEntry(AgentService service)
    {
        if (service.Meta.TryGetValue("json", out var json))
        {
            var s = JsonSerializer.Deserialize<MembershipEntry>(json)!;
            s.IAmAliveTime = DateTime.Now - TimeSpan.FromSeconds(10);
            return s;
        }
        throw new InvalidOperationException($"AgentService do not contains json meta");
    }

    public TimeSpan MaxStaleness => TimeSpan.FromSeconds(30);
    public bool     IsUpdatable  => true;
}