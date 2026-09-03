namespace ArgonSharedLogicTest.Clustering;

using System.Reflection;
using Argon.Features.Clustering;

/// <summary>
/// Whether every role can actually read the settings its own code asks for.
/// </summary>
/// <remarks>
/// <para><b>This is the check that was missing when the file redirect broke in production.</b> The
/// endpoint's handler takes <c>IOptions&lt;StorageOptions&gt;</c>; the feature that maps it declared
/// only its own section. Declaring is what binds, so on the role that serves that endpoint the
/// handler got a default-constructed instance — no regional origins, so it redirected to a relative
/// path the caller resolved against the API and got a 404, and no cache window, so every image paid
/// for the round trip again. The process was healthy throughout and logged nothing.</para>
///
/// <para><b>No functional test could have caught it</b>, and that is the reason this one is here
/// rather than another scenario. The integration suite composes every role into a single process, so
/// some feature always declares the section and the binding always exists; the defect only appears
/// where the roles are split, which is production. A check that walks the roles as declared is the
/// only place it is visible before a deploy.</para>
///
/// <para>Per role rather than per feature, because a feature is allowed to read what a feature it
/// requires declared — that is what requiring one is for. What is not allowed is a role that maps
/// code reaching for a section nothing in that role owns.</para>
/// </remarks>
[TestFixture]
public class RoleOptionsDeclarationTests
{
    /// <summary>
    /// The production roles, forced into the process before anything asks what roles there are.
    /// </summary>
    /// <remarks>
    /// The scan looks at loaded assemblies, and .NET loads one when something first needs a type from
    /// it — so without this the catalog contains the fixtures' own roles and none of the real ones,
    /// and every case passes while checking nothing. Naming a type here is what loads it.
    /// </remarks>
    private static readonly Assembly Product = typeof(Argon.Api.Clustering.EntryPointRole).Assembly;

    private static readonly ArgonClusterCatalog Catalog = ArgonClusterCatalog.Build();

    private static readonly ClusterScanScope Scope = ClusterScanScope.Default();

    private static readonly OptionsUsageScanner Scanner = new(Scope);

    private static readonly ServiceRegistrationScanner Registrations = new(Scope);

    private static IEnumerable<TestCaseData> Roles()
        => Catalog.Roles.Values
              .OrderBy(role => role.Id.Value)
              .Select(role => new TestCaseData(role).SetName($"{{m}}({role.Id.Value})"));

    /// <summary>
    /// The roles that ship are among the ones checked.
    /// </summary>
    /// <remarks>
    /// Its own test because the failure it guards is invisible: if the scan stops seeing the product
    /// assembly, every case above still runs and still passes — over the fixtures' toy roles. A check
    /// that cannot fail is worse than none, because it is reported as coverage.
    /// </remarks>
    [Test]
    public void The_roles_that_ship_are_the_ones_being_checked()
    {
        var names = Catalog.Roles.Keys.Select(id => id.Value).ToHashSet();

        Assert.That(Product, Is.Not.Null);
        Assert.That(names, Is.SupersetOf(new[] { "entrypoint", "core", "media", "aegis", "jobs" }),
            $"the catalog holds {string.Join(", ", names.Order())} — the production roles are not in "
          + "it, so every case in this fixture is checking the fixtures' own roles");
    }

    [TestCaseSource(nameof(Roles))]
    public void Every_setting_a_role_reads_is_declared_by_one_of_its_features(RoleDescriptor role)
    {
        var declared = role.Features.Ordered
           .SelectMany(feature => feature.Options.Select(option => option.OptionsType))
           .ToHashSet();

        var undeclared = role.Features.Ordered
           .SelectMany(feature => Scanner.UsagesOf(feature.FeatureType, ActivatableOn(role))
                                         .Where(used => !declared.Contains(used))
                                         .Select(used => (Feature: feature.Name, Setting: used.Name)))
           .Distinct()
           .OrderBy(x => x.Feature)
           .ToArray();

        Assert.That(undeclared, Is.Empty,
            $"role '{role.Id.Value}' runs code that reads settings no feature of it declared, so those "
          + "settings bind to defaults and the code runs on them silently: "
          + string.Join(", ", undeclared.Select(x => $"{x.Feature} reads {x.Setting}")));
    }

    /// <summary>
    /// The same rule for the grains a role activates, which the walk above cannot see.
    /// </summary>
    /// <remarks>
    /// <para>A grain is reached from the role's grain registry, not from any feature's code, so a
    /// feature walk never arrives at its constructor. That is the gap the trust grain fell through:
    /// hosted on <c>core</c> with <c>IOptions&lt;ReportSystemOptions&gt;</c> in its constructor and no
    /// feature on <c>core</c> declaring the section, it read <c>IsEnabled</c> as false and reported
    /// the default score of an empty <c>TrustScoring</c> — zero, "Locked" in the console — for every
    /// user in production, while the report grain a role away recorded resolutions it would never
    /// count.</para>
    ///
    /// <para>Only settings some feature declares somewhere are checked. A grain taking the options of
    /// a library or the framework is asking for something no feature owns, and this fixture is about
    /// ownership.</para>
    /// </remarks>
    [TestCaseSource(nameof(Roles))]
    public void Every_setting_a_hosted_grain_reads_is_declared_by_one_of_the_roles_features(RoleDescriptor role)
    {
        var declaredAnywhere = Catalog.Roles.Values
           .SelectMany(r => r.Features.Ordered)
           .SelectMany(feature => feature.Options.Select(option => option.OptionsType))
           .ToHashSet();

        var declaredHere = role.Features.Ordered
           .SelectMany(feature => feature.Options.Select(option => option.OptionsType))
           .ToHashSet();

        var undeclared = role.HostedGrains
           .SelectMany(grain => Scanner.ConstructedWith(grain)
                                       .Where(used => declaredAnywhere.Contains(used) && !declaredHere.Contains(used))
                                       .Select(used => (Grain: grain.Name, Setting: used.Name)))
           .Distinct()
           .OrderBy(x => x.Grain)
           .ToArray();

        Assert.That(undeclared, Is.Empty,
            $"role '{role.Id.Value}' hosts grains built with settings no feature of it declared, so those "
          + "grains are activated on default instances and run on them silently: "
          + string.Join(", ", undeclared.Select(x => $"{x.Grain} takes {x.Setting}")));
    }

    /// <summary>
    /// Whether this role could actually build a type some framework adopted by convention.
    /// </summary>
    /// <remarks>
    /// <para>Only reflection-adopted families are asked this, and only because adoption is not the
    /// same as being usable. MVC hands every role that maps controllers every controller in the
    /// product, the identity server's among them — but those need services the Aegis features alone
    /// register, so on any other role activation fails before a line of them runs. A setting such a
    /// type would have read is not a setting that role is missing.</para>
    ///
    /// <para>Only the product's own types are checked. A dependency from the framework — a grain
    /// factory, a logger, <c>IOptions</c> itself — is provided by the host on every role, and
    /// demanding that a feature register it would refuse every type in the product.</para>
    /// </remarks>
    private static Func<Type, bool> ActivatableOn(RoleDescriptor role)
    {
        var registered = role.Features.Ordered
           .SelectMany(feature => Registrations.RegistrationsOf(feature.FeatureType))
           .ToArray();

        var scanned = Scope.Assemblies.ToHashSet();

        bool Available(Type dependency)
            => !scanned.Contains(dependency.Assembly)
            || (dependency.IsGenericType && dependency.GetGenericTypeDefinition().Name.StartsWith("IOptions"))
            || registered.Any(dependency.IsAssignableFrom);

        return type => type.GetConstructors()
           .Any(constructor => constructor.GetParameters().All(p => Available(p.ParameterType)));
    }
}
