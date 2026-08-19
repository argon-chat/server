namespace Argon.Features.Clustering.Regions;

using Orleans.Messaging;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// Finds another region's gateways by resolving one DNS name.
/// </summary>
/// <remarks>
/// <para>The obvious choice would be the clustering provider the silos themselves use — Redis — and
/// it is the wrong one across a region boundary. It would mean every region's process opening a
/// connection to every other region's membership store, so a Redis that today is reachable only from
/// its own cluster becomes reachable from all of them, and a client that cannot reach it cannot even
/// discover that the region exists. Gateways are the only thing a region has to expose anyway, since
/// that is what a client connects to.</para>
///
/// <para>Orleans calls <see cref="GetGateways"/> again every <see cref="MaxStaleness"/>, so a name
/// is enough: the silos behind it are replaced by every deployment, and each refresh picks up
/// whatever is there now. Returning an empty list is not an error — it is a region with no gateway
/// up, which is a state that resolves itself.</para>
/// </remarks>
public sealed class RegionGatewayListProvider(
    string region,
    string host,
    int port,
    TimeSpan refreshPeriod,
    ILogger logger) : IGatewayListProvider
{
    public TimeSpan MaxStaleness => refreshPeriod;

    [Obsolete("Orleans treats every provider as updatable; the interface still declares it.")]
    public bool IsUpdatable => true;

    public Task InitializeGatewayListProvider() => Task.CompletedTask;

    public async Task<IList<Uri>> GetGateways()
    {
        // A literal address is the common case in tests and in any deployment that pins one, and
        // Dns.GetHostAddressesAsync would answer it anyway — but only after a lookup, and only if the
        // resolver is willing. Parsing first keeps that path from depending on DNS at all.
        if (IPAddress.TryParse(host, out var literal))
            return [new IPEndPoint(literal, port).ToGatewayUri()];

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);

            var gateways = addresses
               .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
               .Select(a => new IPEndPoint(a, port).ToGatewayUri())
               .ToList();

            if (gateways.Count == 0)
                logger.LogWarning("Region '{Region}' gateway name '{Host}' resolved to nothing", region, host);

            return gateways;
        }
        catch (SocketException e)
        {
            // Every refresh calls this, so a name that is down would otherwise log a stack trace on a
            // timer. The region simply has no gateways until it resolves again, which the caller
            // already handles.
            logger.LogDebug(e, "Region '{Region}' gateway name '{Host}' did not resolve", region, host);
            return [];
        }
    }
}
