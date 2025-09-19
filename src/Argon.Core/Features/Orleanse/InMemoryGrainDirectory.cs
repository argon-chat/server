namespace Argon.Api.Features;

using global::Orleans.GrainDirectory;
using global::Orleans.Runtime.Hosting;

public static class InMemoryGrainDirectoryEx
{
    public static ISiloBuilder AddInMemoryGrainDirectory(this ISiloBuilder builder, string name)
    {
        builder.ConfigureServices(q => { q.AddSingleton<InMemoryGrainDirectory>(); });

        return builder.AddGrainDirectory(name, (q, w) => q.GetRequiredService<InMemoryGrainDirectory>());
    }
}

public class InMemoryGrainDirectory : IGrainDirectory
{
    private readonly ConcurrentDictionary<GrainId, GrainAddress> Grains = new();

    public async Task<GrainAddress?> Register(GrainAddress address)
        => Grains.TryAdd(address.GrainId, address) ? address : null;

    public async Task Unregister(GrainAddress address)
        => Grains.TryRemove(address.GrainId, out _);

    public async Task<GrainAddress?> Lookup(GrainId grainId)
    {
        if (Grains.TryGetValue(grainId, out var addr))
            return addr;
        return null;
    }

    public Task UnregisterSilos(List<SiloAddress> siloAddresses)
        => Task.CompletedTask;
}