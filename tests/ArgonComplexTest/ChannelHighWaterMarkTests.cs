namespace ArgonComplexTest.Tests;

using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using Argon.Entities;
using Argon.Services.L1L2;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// <c>lastMessageId</c> moves constantly and is served to every client; those two facts pull in
/// opposite directions and this is where they are pinned against each other.
/// </summary>
/// <remarks>
/// It used to be inside the hash behind <c>SpaceVersions.channels</c>, so in any space with traffic
/// the channels token changed every time the two-minute cache entry refilled. That token is what
/// decides whether <c>GetSnapshot</c> takes its expensive branch — one grain call per visible channel
/// — so the versioned bootstrap built to avoid the fan-out was defeated by a counter, and defeated
/// hardest in exactly the busy spaces it was built for.
/// <para>
/// Taking it out of the hash is only half the trade. The other half is that the number now has to
/// arrive by a route that does not depend on the cache being refilled, which is the Redis cell the
/// channel grain writes on every send. Both halves are here because either one alone is a bug: a
/// stable token serving a two-minute-old counter, or a fresh counter that still re-sends the whole
/// channel list on every reconnect.
/// </para>
/// </remarks>
[TestFixture]
public class ChannelHighWaterMarkTests : TestBase
{
    /// <summary>
    /// Nothing about the snapshot's <em>content</em> notices this regression — the answer stays
    /// correct, it just costs a fan-out per reconnect — so the assertion has to be about the token.
    /// </summary>
    /// <remarks>
    /// The cache refill in the middle is the whole test, and leaving it out is how this passes on the
    /// very code it is meant to catch. The token is minted inside the cache factory, and a message
    /// send does not invalidate the space read cache — only a channel edit does. So two snapshots
    /// taken back to back read the same stored token whether or not the counter is in the hash, and
    /// the regression this fixture exists for only ever showed up on the two-minute refill. Forcing
    /// the refill is what makes the two implementations answer differently.
    ///
    /// <para>The wait before it matters for the same reason: the durable write is coalesced onto a
    /// timer, so refilling immediately would rebuild the entry from a row that still reads zero — the
    /// old code would hash the same zero it hashed the first time and pass again.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task Sending_a_message_leaves_the_channels_token_alone(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "high-water", ct);

        var spaces = GetServerService(scope.ServiceProvider);
        var first  = await spaces.GetSpaceSnapshot(spaceId, null, ct);

        await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, "moves the high-water mark", new IonArray<IMessageEntity>([]), 1, null, ct);

        await WaitForStoredHighWaterMarkAsync(channelId, ct);

        await FactoryAsp.Services.GetRequiredService<ISpaceReadCache>().InvalidateAsync(spaceId);

        var second = await spaces.GetSpaceSnapshot(spaceId, first.versions, ct);

        Assert.Multiple(() =>
        {
            Assert.That(second.versions.channels, Is.EqualTo(first.versions.channels),
                "no channel was created, renamed, moved or re-permissioned — somebody only talked");

            Assert.That(second.channels, Is.Null,
                "the caller's token still matches, so the channel list must not be sent again");
        });
    }

    /// <summary>
    /// The counter has to reach the client by a route the cache is not on, or holding the token
    /// steady would just mean serving a number that is up to two minutes old.
    /// </summary>
    /// <remarks>
    /// This is the read half of a contract whose write half lives in <c>ChannelGrain</c>: the cell at
    /// <c>chan:last:{channelId}</c>. Nothing in the type system connects the two, so a failure here
    /// means one side changed the key, the encoding, or stopped writing — the read side degrades
    /// silently to the database value rather than throwing, which is right in production and invisible
    /// without this test.
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task A_new_message_is_visible_in_the_snapshot_before_the_cache_expires(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "high-water-fresh", ct);

        var spaces = GetServerService(scope.ServiceProvider);

        // Fills the channel cache while the channel is still empty, which is the state the stale path
        // would serve back: without the Redis read, the snapshot below reports 0 for two minutes.
        await spaces.GetSpaceSnapshot(spaceId, null, ct);

        var messageId = await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, "the freshest thing here", new IonArray<IMessageEntity>([]), 1, null, ct);

        // The grain writes the cell off the send path so delivery is not held up by it, so the value
        // can land a beat after SendMessage returns. Polling is the difference between proving the
        // cell is read and failing on a loaded CI box.
        var served = 0L;

        for (var attempt = 0; attempt < 20 && served != messageId; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(250, ct);

            var snapshot = await spaces.GetSpaceSnapshot(spaceId, null, ct);

            served = snapshot.channels!.Value.ToList()
               .Single(c => c.channel.channelId == channelId)
               .channel.lastMessageId;
        }

        Assert.That(served, Is.EqualTo(messageId),
            "the snapshot served a stale high-water mark; the Redis cell was not read, or not written");
    }

    /// <summary>
    /// Waits until the channel row itself carries the new id, which is what a cache refill would read.
    /// </summary>
    /// <remarks>
    /// Polls the row rather than sleeping for the flush interval: the interval is the grain's business
    /// and a test that hard-codes it breaks the day somebody tunes it. Fails loudly on timeout instead
    /// of continuing, because a zero row here would make the assertions below vacuous rather than red.
    /// </remarks>
    private async Task WaitForStoredHighWaterMarkAsync(Guid channelId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var db = await FactoryAsp.Services
               .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
               .CreateDbContextAsync(ct);

            var stored = await db.Channels
               .AsNoTracking()
               .Where(c => c.Id == channelId)
               .Select(c => c.LastMessageId)
               .FirstOrDefaultAsync(ct);

            if (stored > 0)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        Assert.Fail("the channel row never took the message id, so the refill below would prove nothing");
    }

}
