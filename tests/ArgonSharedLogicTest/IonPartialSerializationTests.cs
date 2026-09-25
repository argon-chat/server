namespace ArgonSharedLogicTest;

using Argon.Features.Clustering;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

/// <summary>
/// A patch handed to a grain crosses the Orleans serializer once the Ion service and the silo are in
/// different pods. Nothing in the co-hosted suite serializes a grain argument, so this is where a
/// patch arriving empty on the far side would show.
/// </summary>
[TestFixture]
public class IonPartialSerializationTests
{
    private static Serializer Serializer()
        => new ServiceCollection()
           .AddArgonSerializer()
           .BuildServiceProvider()
           .GetRequiredService<Serializer>();

    [Test]
    public void A_patch_keeps_modified_cleared_and_untouched_apart()
    {
        var target = Guid.NewGuid();
        var patch = new IonPartial<BroadcastSettings>()
           .Modify(x => x.targets, new IonArray<Guid>(new List<Guid> { target }))
           .Modify(x => x.duckingDb, -12)
           .Remove(x => x.maxTransmitSeconds);

        var serializer = Serializer();
        var carried    = serializer.Deserialize<IonPartial<BroadcastSettings>>(serializer.SerializeToArray(patch))!;

        Assert.Multiple(() =>
        {
            Assert.That(carried.Count, Is.EqualTo(3));
            Assert.That(carried.GetField(x => x.targets).Value.Values, Is.EqualTo(new[] { target }));
            Assert.That(carried.GetField(x => x.duckingDb).Value, Is.EqualTo(-12));
            Assert.That(carried.StateOf(nameof(BroadcastSettings.maxTransmitSeconds)), Is.EqualTo(PartialState.Removed));
            Assert.That(carried.StateOf(nameof(BroadcastSettings.overlap)), Is.EqualTo(PartialState.None));
            Assert.That(carried.StateOf(nameof(BroadcastSettings.chirp)), Is.EqualTo(PartialState.None));
        });
    }

    [Test]
    public void An_empty_patch_arrives_empty()
    {
        var serializer = Serializer();
        var carried    = serializer.Deserialize<IonPartial<BroadcastSettings>>(serializer.SerializeToArray(new IonPartial<BroadcastSettings>()))!;

        Assert.That(carried.Count, Is.Zero);
    }
}
