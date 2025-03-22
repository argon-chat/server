namespace Argon.Features.RegionalUnit;

using System.Net.NetworkInformation;
using GeoIP;


public record NodeRoute(LinkedList<InternetNodePosition> graph, float effectivity);

public record InternetNodePosition(IPAddress ip, long rtt, ECEFCoordinate pos);

public record NodeJump(IPAddress ip, int step, long rtt);

public record ArgonUnitDto(ECEFCoordinate pos, string ip);

public record struct ECEFCoordinate(decimal n, decimal e2, decimal h);

// { "pos": { "x": "2770168.7109373207", "y": "1616434.8235493843", "z": "5494585.226584545" }, "ip": "" }

public interface IArgonClusterRouter
{
    Task<NodeRoute> ComputeRouteAsync(string dc);
    Task<NodeRoute> ComputeRouteAsync(IPAddress targetAddress);
    Task<float>     ComputeEffectivity(string dc);
}


public class ClusterRouter( IGeoIp geoIp) : IArgonClusterRouter
{
    public async Task<NodeRoute> ComputeRouteAsync(string dc)
    {
        //if (argonUnitOptions.Value.datacenter.Equals(dc, StringComparison.InvariantCultureIgnoreCase))
        //    throw new InvalidOperationException($"Cannot compute route to self node");
        return null;
    }

    public async Task<NodeRoute> ComputeRouteAsync(IPAddress targetAddress)
        => throw new InvalidOperationException();

    public async Task<float> ComputeEffectivity(string dc)
        => 1;

    private async Task<float> ComputeEffectivity(
        LinkedListNode<InternetNodePosition> at,
        LinkedListNode<InternetNodePosition> to)
        => throw new InvalidOperationException();

    private async Task<ECEFCoordinate> RetrieveECEFAsync(IPAddress address)
        => throw new InvalidOperationException();

    private unsafe static List<NodeJump> GetJumps(IPAddress ip)
    {
        Span<byte> data    = stackalloc byte[256];
        var        arr     = data.ToArray();
        const int  maxHops = 30;

        var ping    = new Ping();
        var options = new PingOptions(1, true);
        var list    = new List<NodeJump>();

        for (var ttl = 1; ttl <= maxHops; ttl++)
        {
            options.Ttl = ttl;
            var reply = ping.Send(ip, 3000, arr, options);

            if (reply.Status is IPStatus.TtlExpired or IPStatus.Success)
            {
                list.Add(new NodeJump(reply.Address, ttl, reply.RoundtripTime));

                if (reply.Status is IPStatus.Success)
                    break;
            }
            else
                list.Add(new NodeJump(IPAddress.Any, ttl, -1));
        }

        return list;
    }
}
