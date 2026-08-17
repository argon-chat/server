namespace ArgonSharedLogicTest.Clustering;

using Argon.Api.Clustering;
using Argon.Features.Clustering;

/// <summary>
/// The analysis that replaced the hand-written <c>GrainRoots</c> declarations.
/// </summary>
/// <remarks>
/// A feature already states what it brings, in the DI registrations it makes. Making it repeat that
/// as a list of analysis roots was two declarations of one fact — and the kind that drifts silently,
/// because nothing forces the second to keep up with the first.
/// </remarks>
[TestFixture]
public class DerivedRootsTests
{
    private static ClusterScanScope Scope()
        => new() { Assemblies = [typeof(CoreRole).Assembly, typeof(IArgonRole).Assembly] };

    private static IReadOnlySet<Type> RootsOf<TFeature>() where TFeature : IArgonFeature
        => new ServiceRegistrationScanner(Scope()).RegistrationsOf(typeof(TFeature));

    [Test]
    public void A_generic_registration_names_its_own_analysis_root()
    {
        // IonProtocolFeature.Configure does AddService<IUserInteraction, UserInteractionImpl>() and
        // seventeen more like it. That list used to be repeated as AddCallRoot<…> declarations.
        var roots = RootsOf<IonProtocolFeature>();

        Assert.That(roots, Does.Contain(typeof(Argon.Services.Ion.UserInteractionImpl))
                              .And.Contain(typeof(Argon.Services.Ion.ChannelInteractionImpl)));
    }

    [Test]
    public void The_walk_follows_the_product_s_own_extension_methods()
    {
        // AppHubFeature.Configure is one line — ctx.Builder.AddSignalRAppHub() — and the hub is
        // registered inside that method, in another assembly.
        Assert.That(RootsOf<AppHubFeature>(), Does.Contain(typeof(Argon.Core.Features.Transport.AppHubServer)));
    }

    [Test]
    public void Convention_registrations_are_expanded_the_way_the_framework_expands_them()
    {
        // Nothing in the IL of AddControllers() or MapBotApi() names a single type: MVC and the bot
        // router discover them. The scanner mirrors that rather than pretending they register nothing.
        Assert.Multiple(() =>
        {
            Assert.That(RootsOf<ControllersFeature>(),
                Does.Contain(typeof(Argon.Features.Storage.FileStorageController)),
                "AddControllers() hands MVC every ControllerBase");
            Assert.That(RootsOf<BotApiFeature>(),
                Does.Contain(typeof(Argon.Api.BotApi.Interfaces.MessagesV1)),
                "MapBotApi() does the same for IBotInterface");
        });
    }

    /// <summary>
    /// The property that makes removing the hand-written lists safe: the derived roots reach every
    /// grain interface the declarations used to.
    /// </summary>
    [Test]
    public void Derived_roots_reach_the_same_grains_the_declarations_did()
    {
        var scope   = Scope();
        var catalog = ArgonClusterCatalog.Build(scope);
        var index   = GrainTypeIndex.Build(scope);
        var scanner = new IlGrainGraphScanner(scope, index);

        var entrypoint = catalog.Require(ArgonRoleId.EntryPoint);
        var reached    = scanner.Analyze(entrypoint.CallRoots.ToArray()).All;

        // A representative slice of what the eighteen hand-written Ion roots used to produce, plus
        // the two that only the convention rules recover.
        Assert.That(reached, Does.Contain(typeof(Argon.Grains.Interfaces.IUserGrain))
                                .And.Contain(typeof(Argon.Grains.Interfaces.ISpaceGrain))
                                .And.Contain(typeof(Argon.Grains.Interfaces.IChannelGrain))
                                .And.Contain(typeof(Argon.Api.Grains.Interfaces.IFileStorageGrain)));
    }

    [Test]
    public void A_feature_name_follows_from_its_type()
    {
        Assert.Multiple(() =>
        {
            Assert.That(FeatureCatalog.Describe<BotPathTokenFeature>().Name, Is.EqualTo("bot-path-token"));
            Assert.That(FeatureCatalog.Describe<HttpClientFeature>().Name, Is.EqualTo("http-client"));
            Assert.That(FeatureCatalog.Describe<CacheFeature>().Name, Is.EqualTo("cache"));

            // Still overridable where the type name is not the name you want.
            Assert.That(FeatureCatalog.Describe<IonProtocolFeature>().Name, Is.EqualTo("ion"));
        });
    }
}
