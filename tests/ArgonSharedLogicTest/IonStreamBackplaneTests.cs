namespace ArgonSharedLogicTest;

using Argon.Core.Features.Transport;
using Argon.Features.Clustering;
using Argon.Features.Clustering.Regions;
using Argon.Services.Ion;
using ArgonContracts;
using ion.runtime.network;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The Redis stream the Ion stream backplane runs on — per region, for the reasons
/// <see cref="BackplaneChannelPrefixTests"/> gives for the SignalR one — and who reads it.
/// </summary>
[TestFixture]
public class IonStreamBackplaneTests
{
    private const string SelfKey = $"{ArgonRegionOptions.SectionName}:{nameof(ArgonRegionOptions.Self)}";

    private static IConfiguration Configuration(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
           .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
           .Build();

    private static string Key(params (string Key, string? Value)[] values)
        => SignalRHubExtensions.IonBackplaneStreamKey(Configuration(values));

    [Test]
    public void Two_regions_do_not_share_a_stream()
    {
        var here  = Key((SelfKey, "ru-3"));
        var there = Key((SelfKey, "eu-1"));

        Assert.Multiple(() =>
        {
            Assert.That(here, Is.Not.EqualTo(there));
            Assert.That(here, Is.EqualTo("ion:streams:ru-3"));
            Assert.That(there, Is.EqualTo("ion:streams:eu-1"));
        });
    }

    [Test]
    public void Naming_the_region_a_deployment_is_already_in_changes_nothing()
        => Assert.That(Key((SelfKey, ArgonDatacenter.Current)), Is.EqualTo(Key()));

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void A_blank_region_falls_back_instead_of_leaving_a_hole(string? configured)
        => Assert.That(Key((SelfKey, configured)), Is.EqualTo($"ion:streams:{ArgonDatacenter.Current}"));

    [Test]
    public void Surrounding_whitespace_is_not_a_second_region()
        => Assert.That(Key((SelfKey, "  eu-1  ")), Is.EqualTo(Key((SelfKey, "eu-1"))));

    /// <summary>The two backplanes of one region name the same region.</summary>
    [Test]
    public void The_stream_and_the_signalr_prefix_agree_on_the_region()
    {
        var configuration = Configuration((SelfKey, "eu-1"));

        Assert.Multiple(() =>
        {
            Assert.That(SignalRHubExtensions.BackplaneChannelPrefix(configuration), Is.EqualTo("argon-bus:eu-1:"));
            Assert.That(SignalRHubExtensions.IonBackplaneStreamKey(configuration), Is.EqualTo("ion:streams:eu-1"));
        });
    }

    /// <summary>
    /// A silo pushes and holds no connections, so everything it could read off the backplane would be
    /// for connections it does not have.
    /// </summary>
    [Test]
    public void A_node_that_serves_no_realtime_stream_only_publishes()
        => Assert.That(Backplane(servesRealtime: false), Is.InstanceOf<PublishOnlyIonBackplane>());

    [Test]
    public void A_node_that_serves_the_realtime_stream_reads_the_backplane()
        => Assert.That(Backplane(servesRealtime: true), Is.InstanceOf<RedisStreamsBackplane>());

    private static IIonStreamBackplane Backplane(bool servesRealtime)
    {
        var builder = WebApplication.CreateSlimBuilder();

        // Never connected to: building the backplane opens nothing, starting it would.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Redis:Backplane:ConnectionString"] = "localhost:1,abortConnect=false"
        });

        builder.AddRealtimeBus();

        if (servesRealtime)
            builder.Services.AddIonProtocol(x => x.AddService<IEventBus, EventBusImpl>());

        return builder.Build().Services.GetRequiredService<IIonStreamBackplane>();
    }
}
