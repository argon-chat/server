namespace ArgonSharedLogicTest;

using Argon.Core.Features.Transport;
using Argon.Features.Clustering;
using Argon.Features.Clustering.Regions;
using Microsoft.Extensions.Configuration;

/// <summary>
/// The Redis channel namespace the SignalR backplane fans out on, which is the only thing keeping
/// one region's broadcasts out of another's.
/// </summary>
/// <remarks>
/// <para>The prefix was the constant <c>argon-bus</c> for the whole deployment. Two regions pointed
/// at one Redis were therefore a single fan-out domain: every broadcast crossed the boundary, data
/// residency was being violated for exactly as long as it worked, and it presented as a working
/// feature rather than as a bug. The Redis profile's database index does not help, because pub/sub
/// is not database-scoped.</para>
///
/// <para>None of that is observable from inside one process at runtime — the failure is that
/// delivery <em>succeeds</em> — so it has to be asserted here, on the derived name itself.</para>
/// </remarks>
[TestFixture]
public class BackplaneChannelPrefixTests
{
    private const string SelfKey = $"{ArgonRegionOptions.SectionName}:{nameof(ArgonRegionOptions.Self)}";

    private static string Prefix(params (string Key, string? Value)[] values)
        => SignalRHubExtensions.BackplaneChannelPrefix(
            new ConfigurationBuilder()
               .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
               .Build());

    [Test]
    public void Two_regions_do_not_share_a_fan_out_domain()
    {
        var here  = Prefix((SelfKey, "ru-3"));
        var there = Prefix((SelfKey, "eu-1"));

        Assert.Multiple(() =>
        {
            Assert.That(here, Is.Not.EqualTo(there),
                "two regions on one Redis would otherwise publish and subscribe on the same channels");
            Assert.That(here, Does.Contain("ru-3"));
            Assert.That(there, Does.Contain("eu-1"));
        });
    }

    /// <summary>
    /// A deployment that declares the region it was already in keeps the prefix it already had.
    /// </summary>
    /// <remarks>
    /// This is what makes adding the region section additive. Changing the prefix splits a rolling
    /// deploy in two — old pods and new pods cannot see each other's broadcasts until the rollout
    /// finishes — so writing down a region that was previously only implied must not be a rename.
    /// </remarks>
    [Test]
    public void Naming_the_region_a_deployment_is_already_in_changes_nothing()
        => Assert.That(Prefix((SelfKey, ArgonDatacenter.Current)), Is.EqualTo(Prefix()),
            "declaring the current region must not change the prefix, and a prefix change is a split brain");

    [Test]
    public void A_deployment_that_configures_no_region_still_gets_a_usable_prefix()
    {
        var prefix = Prefix();

        Assert.Multiple(() =>
        {
            Assert.That(prefix, Does.StartWith("argon-bus:"));
            Assert.That(prefix, Does.EndWith(":"),
                "the region has to end as its own segment rather than run into the hub name");
            Assert.That(prefix, Does.Contain(ArgonDatacenter.Current));
            Assert.That(prefix, Is.EqualTo(Prefix()),
                "same configuration, same prefix — every pod in a region derives this independently");
        });
    }

    /// <summary>
    /// A blank region is an unset one, not an empty segment.
    /// </summary>
    /// <remarks>
    /// Taking a blank value literally would put every region that got its configuration wrong back
    /// into one shared fan-out domain, which is the failure this whole derivation exists to prevent.
    /// </remarks>
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void A_blank_region_falls_back_instead_of_leaving_a_hole(string? configured)
        => Assert.That(Prefix((SelfKey, configured)), Is.EqualTo(Prefix()));

    [Test]
    public void Surrounding_whitespace_is_not_a_second_region()
        => Assert.That(Prefix((SelfKey, "  eu-1  ")), Is.EqualTo(Prefix((SelfKey, "eu-1"))),
            "a stray space in one region's configuration would otherwise split that region's own pods");
}
