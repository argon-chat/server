namespace ArgonComplexTest;

using Argon.Core.Features.Logic;
using Argon.Services;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

/// <summary>
/// What the read-state cache carries, and what happens to the entries a deploy inherits.
/// </summary>
/// <remarks>
/// <para>The cached value used to hold a message id and a mention count and nothing else, so a cache
/// hit rebuilt every entry with a null space while a miss read the real one off the row. The client
/// saw <c>spaceId</c> on the first badge fetch after a cold cache and nothing for the two hours that
/// entry lived — same user, same channel, two answers.</para>
///
/// <para>Fixing the encoding is the easy half. The half worth a test is the one a rollout hits: the
/// old two-field values do not disappear when the new build starts, they live out their TTL, and a
/// reader that treats a short value as malformed would take every user's badges down for two hours
/// on deploy day — a worse bug than the one being fixed.</para>
/// </remarks>
[TestFixture]
public class ReadStateCacheTests : TestBase
{
    private const int CacheDb = 6;

    private static string CacheKey(Guid userId) => $"read_state:{userId}";

    private async Task WriteRawAsync(Guid userId, Guid channelId, string value)
    {
        var pool = FactoryAsp.Services.GetRequiredKeyedService<IRedisPoolConnections>(RedisProfiles.Cache);

        await using var scope = pool.Rent();

        await scope.GetDatabase(CacheDb).HashSetAsync(CacheKey(userId), channelId.ToString(), value);
    }

    /// <summary>
    /// An entry written by the previous build is read, not rejected.
    /// </summary>
    /// <remarks>
    /// Written straight into Redis in the old shape rather than by running the old code, because the
    /// old code is gone — and this is the one case where the value under test is one the current build
    /// cannot produce. A space of null is the honest answer for an entry that never recorded one.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_value_from_the_previous_encoding_still_reads(CancellationToken ct = default)
    {
        var userId    = Guid.NewGuid();
        var channelId = Guid.NewGuid();

        await WriteRawAsync(userId, channelId, "4200:3");

        var states = await FactoryAsp.Services
           .GetRequiredService<IReadStateService>()
           .GetAllReadStatesAsync(userId, ct);

        var entry = states.SingleOrDefault(state => state.ChannelId == channelId);

        Assert.That(entry, Is.Not.Null, "a two-field value read as malformed and the whole fetch was lost");

        Assert.Multiple(() =>
        {
            Assert.That(entry!.LastReadMessageId, Is.EqualTo(4200));
            Assert.That(entry.MentionCount, Is.EqualTo(3));
            Assert.That(entry.SpaceId, Is.Null, "an entry that never carried a space cannot invent one");
        });
    }

    /// <summary>
    /// And an entry written by this build carries the space back out again.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_value_from_the_current_encoding_carries_its_space(CancellationToken ct = default)
    {
        var userId    = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var spaceId   = Guid.NewGuid();

        await WriteRawAsync(userId, channelId, $"4200:3:{spaceId}");

        var states = await FactoryAsp.Services
           .GetRequiredService<IReadStateService>()
           .GetAllReadStatesAsync(userId, ct);

        var entry = states.SingleOrDefault(state => state.ChannelId == channelId);

        Assert.That(entry, Is.Not.Null);
        Assert.That(entry!.SpaceId, Is.EqualTo(spaceId),
            "the space went into the cache and did not come back, which is the defect this encoding exists to fix");
    }
}
