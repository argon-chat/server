namespace ArgonSharedLogicTest.Cache;

using Argon.Services.L1L2;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using System.Buffers;

/// <summary>
/// A generated Ion contract has to survive a round trip through the cache.
/// </summary>
/// <remarks>
/// <c>IonArray&lt;T&gt;</c> serialises to JSON and then cannot be read back — the failure lands on the
/// first cache <em>hit</em>, never on the write, so it looks like an intermittent 500 under load and
/// nothing at all in a single-request test.
/// </remarks>
[TestFixture]
public class IonHybridCacheSerializerTests
{
    private readonly IonHybridCacheSerializerFactory factory = new();

    [Test]
    public void The_factory_claims_a_contract_that_carries_an_ion_array()
    {
        Assert.Multiple(() =>
        {
            Assert.That(factory.TryCreateSerializer<List<SpaceMember>>(out _), Is.True,
                "a list of members carries IonArray<SpaceMemberArchetype> two levels down");
            Assert.That(factory.TryCreateSerializer<IonArray<ChannelGroup>>(out _), Is.True);
        });
    }

    [Test]
    public void The_factory_leaves_everything_else_alone()
        => Assert.Multiple(() =>
        {
            Assert.That(factory.TryCreateSerializer<List<Guid>>(out _), Is.False);
            Assert.That(factory.TryCreateSerializer<string>(out _), Is.False);
            Assert.That(factory.TryCreateSerializer<Dictionary<string, int>>(out _), Is.False);
        });

    [Test]
    public void A_member_survives_the_round_trip()
    {
        Assert.That(factory.TryCreateSerializer<List<SpaceMember>>(out var serializer), Is.True);

        var spaceId = Guid.NewGuid();
        var userId  = Guid.NewGuid();

        List<SpaceMember> members =
        [
            new(userId, spaceId, DateTime.UtcNow, Guid.NewGuid(), Sample(userId),
                new IonArray<SpaceMemberArchetype>([new SpaceMemberArchetype(Guid.NewGuid(), Guid.NewGuid())]))
        ];

        var buffer = new ArrayBufferWriter<byte>();
        serializer!.Serialize(members, buffer);

        var read = serializer.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory));

        Assert.Multiple(() =>
        {
            Assert.That(read, Has.Count.EqualTo(1));
            Assert.That(read[0].userId, Is.EqualTo(userId));
            Assert.That(read[0].archetypes.ToList(), Has.Count.EqualTo(1));
            Assert.That(read[0].archetypes.ToList()[0].archetypeId,
                Is.EqualTo(members[0].archetypes.ToList()[0].archetypeId));
        });
    }

    private static ArgonUser Sample(Guid userId)
        => new(userId, "sample", "Sample", null, UserFlag.NONE);
}

/// <summary>
/// The factory has to win against the one HybridCache installs by default.
/// </summary>
[TestFixture]
public class IonHybridCacheWiringTests
{
    [Test]
    public async Task A_contract_round_trips_through_a_real_hybrid_cache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache().AddSerializerFactory<IonHybridCacheSerializerFactory>();

        await using var provider = services.BuildServiceProvider();
        var             cache    = provider.GetRequiredService<HybridCache>();

        var userId = Guid.NewGuid();

        List<SpaceMember> Build() =>
        [
            new(userId, Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(),
                new ArgonUser(userId, "sample", "Sample", null, UserFlag.NONE),
                new IonArray<SpaceMemberArchetype>([new SpaceMemberArchetype(Guid.NewGuid(), Guid.NewGuid())]))
        ];

        await cache.GetOrCreateAsync("members", _ => new ValueTask<List<SpaceMember>>(Build()));

        // The second call is the one that matters: the first returns the object it was handed, the
        // second reads it back out of the cache and has to deserialize.
        var second = await cache.GetOrCreateAsync("members",
            _ => new ValueTask<List<SpaceMember>>(throwIfCalled()));

        Assert.That(second[0].userId, Is.EqualTo(userId));
        return;

        static List<SpaceMember> throwIfCalled()
            => throw new InvalidOperationException("the entry should have come from the cache");
    }
}
