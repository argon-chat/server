namespace Argon.Features.RegionalUnit;

using Microsoft.Extensions.DependencyInjection;

public interface IArgonRegionalBus
{
    Task<string>       GetSelfDcAsync(CancellationToken ct = default);
    Task<List<string>> GetAllDcsAsync(CancellationToken ct = default);
    Task<ArgonUnitDto> GetUnitDataByDcAsync(string dc, CancellationToken ct = default);
}

public class ArgonRegionalBus([FromKeyedServices("dc")] string dc) : IArgonRegionalBus
{
    public Task<string> GetSelfDcAsync(CancellationToken ct = default)
        => Task.FromResult(dc);

    public async Task<List<string>> GetAllDcsAsync(CancellationToken ct = default)
        => [dc];

    public Task<ArgonUnitDto> GetUnitDataByDcAsync(string dc, CancellationToken ct = default)
        => throw new NotImplementedException(); // TODO
}