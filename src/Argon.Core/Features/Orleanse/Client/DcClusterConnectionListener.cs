namespace Argon.Api.Features.Orleans.Client;

using Argon.Features;


public enum ArgonDataCenterStatus
{
    CREATED,
    WAIT_CONNECT,
    OFFLINE,
    ONLINE,
    MAINTENANCE
}


public class DcClusterConnectionListener(ILogger<DcClusterConnectionListener> logger, IArgonDcRegistry registry, [FromKeyedServices("dc")] string dc) : IClusterConnectionStatusObserver
{
    public void NotifyGatewayCountChanged(int currentNumberOfGateways, int previousNumberOfGateways, bool connectionRecovered)
    {
        logger.LogInformation("NotifyGatewayCountChanged {currentNumberOfGateways}, {previousNumberOfGateways}, {connectionRecovered}",
            currentNumberOfGateways, previousNumberOfGateways, connectionRecovered);

        if (currentNumberOfGateways == 0)
            return;
        if (!connectionRecovered)
            return;

        if (!registry.TryGet(dc, out var self))
            throw new NotSupportedException($"Datacenter {dc} not found in cluster registry");
        registry.Upsert(self with
        {
            status = ArgonDataCenterStatus.ONLINE
        });
    }

    public void NotifyClusterConnectionLost()
    {
        if (!registry.TryGet(dc, out var self))
            throw new NotSupportedException($"Datacenter {dc} not found in cluster registry");
        logger.LogWarning("NotifyClusterConnectionLost {dc} lost connection, going to offline", self.dc);
        registry.Upsert(self with
        {
            status = ArgonDataCenterStatus.OFFLINE
        });
    }
}