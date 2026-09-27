namespace ArgonSharedLogicTest.Cache;

using Argon.Services.L1L2;
using ArgonContracts;

/// <summary>
/// The status emoji lookup behind <c>ExpressionItemDirectoryGrain</c>: misses loaded together, absent ids
/// cached too, an entry dropped by the expressions grain loaded again.
/// </summary>
[TestFixture]
public class ExpressionItemCacheTests
{
    private static readonly StatusEmoji Wave  = Emoji("wave");
    private static readonly StatusEmoji Party = Emoji("party");

    [Test]
    public async Task Misses_are_loaded_in_one_call_and_then_served_from_the_cache()
    {
        await using var cluster = new HybridCacheCluster();
        var silo  = cluster.Silo(ionSerializer: true);
        var db    = new Rows(Wave, Party);
        var gone  = Guid.NewGuid();
        List<Guid> ids = [Wave.itemId, Party.itemId, gone, Wave.itemId];

        var first  = await ExpressionItemCache.ResolveAsync(silo, ids, db.LoadAsync);
        var second = await ExpressionItemCache.ResolveAsync(silo, ids, db.LoadAsync);

        Assert.Multiple(() =>
        {
            Assert.That(db.Loads, Has.Count.EqualTo(1), "the second read went to the database");
            Assert.That(db.Loads[0], Is.EquivalentTo(new[] { Wave.itemId, Party.itemId, gone }), "the misses were not loaded together");
            Assert.That(first, Is.EquivalentTo(second));
            Assert.That(first[Wave.itemId], Is.EqualTo(Wave));
            Assert.That(first.ContainsKey(gone), Is.False);
        });
    }

    [Test]
    public async Task Another_silo_reads_live_and_absent_entries_from_Redis()
    {
        await using var cluster = new HybridCacheCluster();
        var db   = new Rows(Wave);
        var gone = Guid.NewGuid();

        await ExpressionItemCache.ResolveAsync(cluster.Silo(ionSerializer: true), [Wave.itemId, gone], db.LoadAsync);
        var read = await ExpressionItemCache.ResolveAsync(cluster.Silo(ionSerializer: true), [Wave.itemId, gone], db.LoadAsync);

        Assert.Multiple(() =>
        {
            Assert.That(db.Loads, Has.Count.EqualTo(1), "an absent id was not cached");
            Assert.That(read, Has.Count.EqualTo(1));
            Assert.That(read[Wave.itemId], Is.EqualTo(Wave), "the entry did not survive Redis");
        });
    }

    [Test]
    public async Task A_dropped_item_is_loaded_again()
    {
        await using var cluster = new HybridCacheCluster();
        var silo = cluster.Silo(ionSerializer: true);
        var db   = new Rows(Wave, Party);

        await ExpressionItemCache.ResolveAsync(silo, [Wave.itemId, Party.itemId], db.LoadAsync);

        db.Delete(Wave.itemId);
        await ExpressionItemCache.InvalidateAsync(silo, [Wave.itemId]);

        var read = await ExpressionItemCache.ResolveAsync(silo, [Wave.itemId, Party.itemId], db.LoadAsync);

        Assert.Multiple(() =>
        {
            Assert.That(db.Loads, Has.Count.EqualTo(2));
            Assert.That(db.Loads[1], Is.EqualTo(new[] { Wave.itemId }), "an entry nobody dropped was loaded again");
            Assert.That(read.Keys, Is.EquivalentTo(new[] { Party.itemId }), "a deleted item still resolves");
        });
    }

    [Test]
    public void An_absent_entry_lives_shorter_than_a_live_one()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpressionItemCache.LiveOptions.Expiration, Is.EqualTo(TimeSpan.FromMinutes(10)));
            Assert.That(ExpressionItemCache.AbsentOptions.Expiration, Is.EqualTo(TimeSpan.FromMinutes(1)));
        });
    }

    private static StatusEmoji Emoji(string name)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ExpressionFormat.Static, name);

    private sealed class Rows(params StatusEmoji[] rows)
    {
        private readonly Dictionary<Guid, StatusEmoji> live = rows.ToDictionary(r => r.itemId);

        public List<List<Guid>> Loads { get; } = [];

        public void Delete(Guid itemId) => live.Remove(itemId);

        public Task<Dictionary<Guid, StatusEmoji>> LoadAsync(List<Guid> ids)
        {
            Loads.Add(ids.ToList());
            return Task.FromResult(ids.Where(live.ContainsKey).ToDictionary(id => id, id => live[id]));
        }
    }
}
