namespace ArgonSharedLogicTest.Clustering;

using Argon.Api.Clustering;
using Argon.Features.Clustering;
using Microsoft.Extensions.Configuration;

/// <summary>
/// The shipped configuration, checked against what the features actually declare.
/// </summary>
[TestFixture]
public class ShippedConfigurationTests
{
    private static ArgonClusterCatalog Catalog()
        => ArgonClusterCatalog.Build(new ClusterScanScope
        {
            Assemblies = [typeof(CoreRole).Assembly, typeof(IArgonRole).Assembly]
        });

    /// <summary>
    /// The configuration committed to the repository, which is what the image carries before any
    /// deployment overlays anything. A role that cannot validate against it cannot start anywhere
    /// until something else fills the gap — which is a fine thing for a secret, and a bad thing for a
    /// setting a person would expect to have a default.
    /// </summary>
    /// <remarks>
    /// Only <c>appsettings.json</c>. <c>appsettings.Production.json</c> is deliberately not read: it
    /// is not tracked, so its contents differ per machine and asserting against it would make this
    /// test pass or fail depending on whose checkout ran it.
    /// </remarks>
    private static IConfiguration Shipped()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "appsettings.json");

        Assert.That(File.Exists(path), Is.True,
            $"'{path}' is missing; the Argon.Api content files did not reach the test output");

        return new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
    }

    [Test]
    public void Every_role_validates_against_the_shipped_configuration()
    {
        var configuration = Shipped();

        Assert.Multiple(() =>
        {
            foreach (var role in Catalog().Roles.Values)
            {
                var report = FeatureConfigurationValidator.Validate(role, configuration);

                Assert.That(report.Errors, Is.Empty,
                    $"role '{role.Id}':{Environment.NewLine}" +
                    string.Join(Environment.NewLine, report.Errors.Select(e => $"  {e}")));
            }
        });
    }

    /// <summary>
    /// Two features owning one section would make a <c>conf.d</c> file ambiguous — whichever file was
    /// read last would win, and neither feature's ownership check would notice.
    /// </summary>
    [Test]
    public void No_two_features_declare_the_same_configuration_section()
    {
        var owners = Catalog().Roles.Values
           .SelectMany(r => r.Features.Ordered)
           .DistinctBy(f => f.Name)
           .SelectMany(f => f.Options.Select(o => (o.Section, Feature: f.Name)))
           .GroupBy(x => x.Section, StringComparer.OrdinalIgnoreCase)
           .Where(g => g.Select(x => x.Feature).Distinct().Count() > 1)
           .ToArray();

        Assert.That(owners, Is.Empty,
            string.Join(Environment.NewLine,
                owners.Select(g => $"'{g.Key}' is claimed by {string.Join(", ", g.Select(x => x.Feature).Distinct())}")));
    }

    /// <summary>
    /// A section nested under another feature's root would slip past the <c>conf.d</c> ownership check,
    /// which compares the first path segment.
    /// </summary>
    [Test]
    public void A_nested_section_belongs_to_the_feature_that_owns_its_root()
    {
        var declared = Catalog().Roles.Values
           .SelectMany(r => r.Features.Ordered)
           .DistinctBy(f => f.Name)
           .SelectMany(f => f.Options.Select(o => (Root: o.Section.Split(':')[0], o.Section, Feature: f.Name)))
           .ToArray();

        var straddling = declared
           .GroupBy(x => x.Root, StringComparer.OrdinalIgnoreCase)
           .Where(g => g.Select(x => x.Feature).Distinct().Count() > 1)
           .ToArray();

        Assert.That(straddling, Is.Empty,
            string.Join(Environment.NewLine, straddling.Select(g =>
                $"root '{g.Key}' is shared by {string.Join(", ", g.Select(x => $"{x.Feature} ({x.Section})").Distinct())}")));
    }

    [Test]
    public void Every_declared_section_binds_without_throwing()
    {
        var configuration = Shipped();

        Assert.Multiple(() =>
        {
            foreach (var feature in Catalog().Roles.Values.SelectMany(r => r.Features.Ordered).DistinctBy(f => f.Name))
            foreach (var binding in feature.Options)
                Assert.That(() => binding.Bind(configuration), Throws.Nothing,
                    $"'{feature.Name}' cannot bind {binding.OptionsType.Name} from '{binding.Section}'");
        });
    }
}
