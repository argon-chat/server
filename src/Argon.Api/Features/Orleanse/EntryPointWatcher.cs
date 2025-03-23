namespace Argon.Features.EntryPoint;

using System.Net.NetworkInformation;
using System.Net.Sockets;
using Api.Features.Orleans.Consul;
using Consul;
using Systems;

public class EntryPointWatcher(
    ILogger<EntryPointWatcher> logger,
    IConsulClient consul,
    IArgonDcRegistry registry,
    [FromKeyedServices("dc")] string dc)
    : RecurringWorkerService<EntryPointWatcher>(logger)
{
    protected async override Task OnCreateAsync(CancellationToken ct = default)
    {
        await Task.Delay(3000, ct).ConfigureAwait(false);
        var ip = GetLocalIPAddress();

        var service = new AgentServiceRegistration
        {
            Name    = IArgonUnitMembership.EntryPointServiceName,
            Address = ip is null ? "0.0.0.0" : ip.ToString(),
            Port    = 5002,
            ID      = $"argon-entry-point-{dc}",
            Checks =
            [
                new AgentServiceCheck
                {
                    CheckID                        = $"{IArgonUnitMembership.LoopBackHealth}.entry.{dc}",
                    TTL                            = TimeSpan.FromSeconds(15),
                    DeregisterCriticalServiceAfter = TimeSpan.FromSeconds(30)
                }
            ],
            Tags =
            [
                IArgonUnitMembership.EntryUnit,
                $"dc-{dc}"
            ],
        };


        var sr = await consul.Agent.ServiceRegister(service, ct);

        sr.Assert();
    }


    private (TTLStatus status, string reason) GetStatus()
    {
        var nearest = registry.GetNearestDc();

        if (nearest is null)
            return (TTLStatus.Warn, "No nearest dc cluster available!");
        if (!nearest.dc.Equals(dc))
            return (TTLStatus.Warn, "No current dc cluster online in registry!");
        return (TTLStatus.Pass, $"Current dc cluster operating normally, registered datacenters: {registry.GetDcCount()}.");
    }

    protected async override Task RunAsync(CancellationToken ct = default)
    {
        var (status, reason) = GetStatus();
        await consul.Agent.UpdateTTL(
            $"{IArgonUnitMembership.LoopBackHealth}.entry.{dc}",
            reason, status, ct);
        await Task.Delay(5000, ct);
    }

    /// <summary>
    /// Gets the address of the local server.
    /// If there are multiple addresses in the correct family in the server's DNS record, the first will be returned.
    /// </summary>
    /// <returns>The server's IPv4 address.</returns>
    internal static IPAddress? GetLocalIPAddress(AddressFamily family = AddressFamily.InterNetwork, string? interfaceName = null)
    {
        var ipAddress         = family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
        var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        var candidates        = new List<IPAddress>();
        foreach (var networkInterface in networkInterfaces)
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up || 
                (!string.IsNullOrWhiteSpace(interfaceName) &&
                !networkInterface.Name.StartsWith(interfaceName, StringComparison.Ordinal))) continue;
            var flag = networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            candidates.AddRange(
                from unicastAddress in networkInterface.GetIPProperties().UnicastAddresses
                where unicastAddress.Address.AddressFamily == family && (!flag || !unicastAddress.Address.Equals(ipAddress))
                select unicastAddress.Address);
        }

        return candidates.Count > 0
            ? PickIPAddress(candidates)
            : throw new OrleansException("Failed to get a local IP address.");
    }

    private static IPAddress? PickIPAddress(IReadOnlyList<IPAddress> candidates)
    {
        IPAddress? rhs = null;
        foreach (var candidate in candidates)
        {
            if (rhs == null || CompareIPAddresses(candidate, rhs))
                rhs = candidate;
        }

        return rhs;

        static bool CompareIPAddresses(IPAddress lhs, IPAddress rhs)
        {
            var addressBytes1 = lhs.GetAddressBytes();
            var addressBytes2 = rhs.GetAddressBytes();
            if (addressBytes1.Length != addressBytes2.Length)
                return addressBytes1.Length < addressBytes2.Length;
            for (var index = 0; index < addressBytes1.Length; ++index)
            {
                if (addressBytes1[index] != addressBytes2[index])
                    return addressBytes1[index] < addressBytes2[index];
            }

            return false;
        }
    }
}