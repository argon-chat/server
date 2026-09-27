namespace ArgonSharedLogicTest.Cache;

using System.Collections.Concurrent;
using Argon.Services.L1L2;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Silos sharing one Redis. Per test rather than per fixture: the assembly runs every test in
/// parallel, and a fixture field would be one test's cluster disposed under another.
/// </summary>
/// <remarks>
/// The shared Redis is a dictionary behind <see cref="IDistributedCache"/>, not
/// <c>MemoryDistributedCache</c>: <c>HybridCache</c> recognises that type and runs without a second
/// level at all.
/// </remarks>
internal sealed class HybridCacheCluster : IAsyncDisposable
{
    private readonly List<ServiceProvider> silos = [];

    public SharedRedis Redis { get; } = new();

    /// <param name="ionSerializer">Registers the serializer factory production registers, for Ion contracts.</param>
    public HybridCache Silo(bool ionSerializer = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache>(Redis);

        var builder = services.AddHybridCache();
        if (ionSerializer)
            builder.AddSerializerFactory<IonHybridCacheSerializerFactory>();

        var provider = services.BuildServiceProvider();
        silos.Add(provider);

        return provider.GetRequiredService<HybridCache>();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var silo in silos)
            await silo.DisposeAsync();
    }
}

internal sealed class SharedRedis : IDistributedCache
{
    private readonly ConcurrentDictionary<string, byte[]> entries = new();
    private int reads;

    public int Reads => Volatile.Read(ref reads);

    public bool Holds(string key) => entries.ContainsKey(key);

    public void Forget(string key) => entries.TryRemove(key, out _);

    public byte[]? Get(string key)
    {
        Interlocked.Increment(ref reads);
        return entries.GetValueOrDefault(key);
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => entries[key] = value;

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        Set(key, value, options);
        return Task.CompletedTask;
    }

    public void Refresh(string key) { }

    public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;

    public void Remove(string key) => Forget(key);

    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        Forget(key);
        return Task.CompletedTask;
    }
}
