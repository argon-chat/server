namespace Argon.Api.Features.Orleans.Client;

/// <summary>
/// Where <c>IArgonDcRegistry</c> believes a datacenter's cluster client is in its lifecycle.
/// </summary>
/// <remarks>
/// <para>This lived next to <c>DcClusterConnectionListener</c> and outlived it. That observer, the
/// datacenter watcher and the connection service were the superseded discovery layer and are gone;
/// the registry they wrote to is still registered on both the client and the silo path, still
/// carries this enum on every entry, and still branches on it.</para>
///
/// <para>What went with them is everything that ever wrote a status. The watcher was the only
/// caller of <c>ArgonDcRegistry.Upsert</c>, so the real registry now stays empty and
/// <c>GetNearestDc</c> always answers null; only <c>ArgonHybridDcRegistry</c>, which nothing
/// registers, still reports <c>ONLINE</c>. That is harmless because the surviving callers ask the
/// registry for the local cluster client and nothing else — it is not evidence that the states are
/// surplus. Do not trim the members that look unreachable, and do not "simplify" the registry's
/// filters to match: replacing both with the region registry is stage 5 of
/// <c>docs/architecture/multi-region.md</c>, and a half-collapsed enum makes that swap look smaller
/// than it is.</para>
/// </remarks>
public enum ArgonDataCenterStatus
{
    CREATED,
    WAIT_CONNECT,
    OFFLINE,
    ONLINE,
    MAINTENANCE
}
