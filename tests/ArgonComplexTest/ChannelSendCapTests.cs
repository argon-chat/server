namespace ArgonComplexTest.Tests;

using Argon.Api.Features.CoreLogic.Messages;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static ChannelTestKit;

/// <summary>
/// The per-channel ceiling on messages a second — the node's guard against a runaway client, as
/// opposed to slow mode, which is a space's tool against a person.
/// </summary>
/// <remarks>
/// <para>The integration host switches the cap off (<c>TestServerConfiguration.Messages</c>), because
/// the rest of the suite sends as fast as it can on purpose. This fixture turns it on for the length
/// of one test by setting the bound <see cref="MessagesOptions"/> instance the channel grain reads on
/// every send, and puts it back afterwards.</para>
///
/// <para>That value is shared by every channel on the host, so while it is set no other fixture may
/// be sending — hence <c>NonParallelizable</c>, which is also what places it with the topology
/// fixtures when the suite is sharded.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class ChannelSendCapTests : TestBase
{
    private const int Cap = 3;

    [Test, CancelAfter(120_000)]
    public async Task A_channel_takes_its_cap_in_a_second_refuses_the_rest_and_takes_more_the_next_second(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "firehose", ChannelType.Text, ct);

        var options = Services.GetRequiredService<IOptions<MessagesOptions>>().Value;
        var shipped = options.PerChannelPerSecond;
        options.PerChannelPerSecond = Cap;

        try
        {
            // Sent together so all of them land inside one second however slow the box is.
            var sends = Enumerable.Range(0, Cap * 2).Select(async i =>
            {
                try
                {
                    await owner.Channels.SendMessage(spaceId, channelId, $"burst {i}", new IonArray<IMessageEntity>([]),
                        Random.Shared.NextInt64(1, long.MaxValue), null, ct);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }).ToList();

            var accepted = (await Task.WhenAll(sends)).Count(x => x);

            Assert.That(accepted, Is.EqualTo(Cap), "the channel did not stop at its cap");

            await Task.Delay(TimeSpan.FromSeconds(1.2), ct);

            Assert.That(async () => await owner.Channels.SendMessage(spaceId, channelId, "a new second", new IonArray<IMessageEntity>([]),
                    Random.Shared.NextInt64(1, long.MaxValue), null, ct),
                Throws.Nothing, "the cap did not open again once the second had passed");
        }
        finally
        {
            options.PerChannelPerSecond = shipped;
        }

        var stored = await owner.Channels.QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(stored.Values, Has.Count.EqualTo(Cap + 1), "a refused message was stored anyway");
    }
}
