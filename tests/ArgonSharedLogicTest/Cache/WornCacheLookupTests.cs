namespace ArgonSharedLogicTest.Cache;

using Argon.Services.L1L2;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;

/// <summary>
/// What <c>HybridCache</c> does with the options and tags the worn-cosmetics read uses, on two or
/// three silos sharing one Redis.
/// </summary>
/// <remarks>
/// <para>Every assertion here is a property of the library that <c>CosmeticsReadGrain</c> is built on
/// and that its own tests cannot see, because an integration host is one silo. The first two pin a
/// bug the grain had: its lookup refused to keep what it found in Redis, so every member list on a
/// silo that had not written the entry went back to Redis for every member. The third pins the trap
/// in the obvious fix: the same lookup without the entry's tags keeps a copy that no drop can
/// reach.</para>
///
/// <para>The shared Redis is a dictionary behind <see cref="IDistributedCache"/>, not
/// <c>MemoryDistributedCache</c>: <c>HybridCache</c> recognises that type and runs without a second
/// level at all, which would make every assertion below pass or fail for the wrong reason.</para>
/// </remarks>
[TestFixture]
public class WornCacheLookupTests
{
    private static readonly Guid Wearer = Guid.NewGuid();

    private static string Key => ICosmeticsCache.WornKey(Wearer);

    [Test]
    public async Task A_miss_is_written_nowhere()
    {
        await using var cluster = new HybridCacheCluster();
        var silo = cluster.Silo();

        Assert.That(await LookUpAsync(silo), Is.Null);
        Assert.That(cluster.Redis.Holds(Key), Is.False, "the miss was written to Redis");

        var ran = false;

        await silo.GetOrCreateAsync(Key, _ =>
        {
            ran = true;
            return ValueTask.FromResult<byte[]?>([1]);
        });

        Assert.That(ran, Is.True, "the miss was kept in memory, so the next read never loaded anything");
    }

    [Test]
    public async Task A_hit_found_in_Redis_is_kept_in_memory()
    {
        await using var cluster = new HybridCacheCluster();
        var writer = cluster.Silo();
        var reader = cluster.Silo();

        await WriteAsync(writer);

        Assert.That(await LookUpAsync(reader), Is.Not.Null);

        cluster.Redis.Forget(Key);
        var reads = cluster.Redis.Reads;

        Assert.That(await LookUpAsync(reader), Is.Not.Null, "the copy found in Redis was not kept");
        Assert.That(cluster.Redis.Reads, Is.EqualTo(reads), "the second lookup went back to Redis");
    }

    [Test]
    public async Task The_copy_kept_in_memory_is_dropped_with_the_entry()
    {
        await using var cluster = new HybridCacheCluster();
        var writer = cluster.Silo();
        var reader = cluster.Silo();

        await WriteAsync(writer);
        Assert.That(await LookUpAsync(reader), Is.Not.Null);

        await reader.RemoveByTagAsync(ICosmeticsCache.WornTag(Wearer));

        Assert.That(await LookUpAsync(reader), Is.Null, "a drop did not reach the copy the lookup kept");
    }

    /// <summary>
    /// The one race <see cref="WornDropLedger"/> cannot see: a silo writes rows it read before a
    /// change, after the others dropped the entry and before the drop reached it.
    /// </summary>
    [Test]
    public async Task A_write_that_trails_the_drop_is_cleared_by_the_second_drop()
    {
        await using var cluster = new HybridCacheCluster();
        var late     = cluster.Silo();
        var writer   = cluster.Silo();
        var onlooker = cluster.Silo();
        var tag      = ICosmeticsCache.WornTag(Wearer);

        await WriteAsync(writer);
        Assert.That(await LookUpAsync(onlooker), Is.Not.Null);

        await writer.RemoveByTagAsync(tag);
        await onlooker.RemoveByTagAsync(tag);
        await Tick();
        await WriteAsync(late);
        await Tick();
        await late.RemoveByTagAsync(tag);

        Assert.That(await LookUpAsync(onlooker), Is.Not.Null,
            "the premise: after one drop the stale write is still served where the drop came first");

        await Tick();

        foreach (var silo in new[] { writer, onlooker, late })
            await silo.RemoveByTagAsync(tag);

        var onWriter   = await LookUpAsync(writer);
        var onOnlooker = await LookUpAsync(onlooker);
        var onLate     = await LookUpAsync(late);

        Assert.Multiple(() =>
        {
            Assert.That(onWriter, Is.Null, "the silo that made the change still serves the stale write");
            Assert.That(onOnlooker, Is.Null, "a silo that heard first still serves the stale write");
            Assert.That(onLate, Is.Null, "the silo that wrote it still serves the stale write");
        });
    }

    private static ValueTask<byte[]?> LookUpAsync(HybridCache silo)
        => silo.GetOrCreateAsync<byte[]?>(Key, static _ => ValueTask.FromResult<byte[]?>(null),
            ICosmeticsCache.WornLookupOptions, ICosmeticsCache.WornTags(Wearer));

    private static ValueTask WriteAsync(HybridCache silo)
        => silo.SetAsync(Key, new byte[] { 1 }, ICosmeticsCache.WornOptions, ICosmeticsCache.WornTags(Wearer));

    /// <summary>Lets the clock move, so a drop and a write are never stamped with the same tick.</summary>
    private static Task Tick() => Task.Delay(5);
}
