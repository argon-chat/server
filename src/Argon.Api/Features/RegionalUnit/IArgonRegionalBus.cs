namespace Argon.Features.RegionalUnit;

using Consul;

public interface IArgonRegionalBus
{
    Task<string>       GetSelfDcAsync(CancellationToken ct = default);
    Task<List<string>> GetAllDcsAsync(CancellationToken ct = default);
    Task<ArgonUnitDto> GetUnitDataByDcAsync(string dc, CancellationToken ct = default);
}

public class ArgonRegionalBus([FromKeyedServices("dc")] string dc, IConsulClient consul) : IArgonRegionalBus
{
    public Task<string> GetSelfDcAsync(CancellationToken ct = default)
        => Task.FromResult(dc);

    public async Task<List<string>> GetAllDcsAsync(CancellationToken ct = default)
    {
        var dcs = await consul.Catalog.Datacenters(ct);

        return dcs.Response.ToList();
    }

    public Task<ArgonUnitDto> GetUnitDataByDcAsync(string dc, CancellationToken ct = default)
        => throw new NotImplementedException(); // TODO
}