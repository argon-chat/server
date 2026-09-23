namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;
using Argon.Features.Cosmetics.Kinds;

/// <summary>
/// What the build refuses to start with.
/// </summary>
/// <remarks>
/// Every check here is one that used to be discoverable only by looking at a profile and noticing
/// it was wrong. A kind is a file, the registry is every file, and the questions a set of files can
/// answer wrongly — two claiming a key, two on one layer of one surface, an axis over a kind nobody
/// wrote — are answered once at configuration with the offending type named.
/// </remarks>
[TestFixture]
public class CosmeticKindRegistryTests
{
    private static CosmeticKindRegistry Shipped()
        => CosmeticKindRegistry.FromAssembly(typeof(ICosmeticKind).Assembly);

    [Test]
    public void The_kinds_this_build_ships_agree_with_each_other()
        => Assert.DoesNotThrow(() => Shipped());

    [Test]
    public void Every_kind_this_build_ships_is_in_the_registry()
    {
        var keys = Shipped().All.Select(kind => kind.Key).ToArray();

        Assert.That(keys, Is.EquivalentTo(new[]
        {
            "profile.frame", "avatar.decoration", "nickname.style",
            "option.font", "option.text-effect"
        }));
    }

    [Test]
    public void A_kind_is_described_without_being_constructed()
    {
        var definition = CosmeticKindRegistry.Describe(typeof(ProfileFrameKind));

        Assert.Multiple(() =>
        {
            Assert.That(definition.Key, Is.EqualTo("profile.frame"));
            Assert.That(definition.DeclaredBy, Does.EndWith(nameof(ProfileFrameKind)));
            Assert.That(definition.Primitive, Is.EqualTo(RenderPrimitive.FrameAssembly));
            Assert.That(definition.RendersOn(CosmeticSurface.ProfileCard), Is.True);
            Assert.That(definition.RendersOn(CosmeticSurface.Avatar), Is.False);
            Assert.That(definition.MaxSlots, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// The feature flag a kind derives, which the console shows and the read path reads.
    /// </summary>
    [Test]
    public void A_kind_gates_itself_on_a_flag_named_after_its_key()
        => Assert.That(CosmeticKindRegistry.Describe(typeof(AvatarDecorationKind)).FeatureFlagKey,
            Is.EqualTo("af.cosmetics.avatar-decoration.active"));

    /// <summary>
    /// A kind exists because a file declares it, so it is on until a row says otherwise.
    /// </summary>
    [Test]
    public void A_kind_with_no_flag_row_is_on()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CosmeticKindDefinition.IsEnabled(flagExists: false, flagSaysEnabled: false), Is.True);
            Assert.That(CosmeticKindDefinition.IsEnabled(flagExists: true, flagSaysEnabled: false), Is.False);
            Assert.That(CosmeticKindDefinition.IsEnabled(flagExists: true, flagSaysEnabled: true), Is.True);
        });
    }

    [Test]
    public void Two_kinds_cannot_claim_one_key()
        => Assert.That(() => CosmeticKindRegistry.FromTypes([typeof(Twin), typeof(TwinAgain)]),
            Throws.TypeOf<CosmeticKindDeclarationException>().With.Message.Contains("already declares"));

    [Test]
    public void Two_kinds_cannot_share_a_layer_on_a_surface()
        => Assert.That(() => CosmeticKindRegistry.FromTypes([typeof(Twin), typeof(Overlapping)]),
            Throws.TypeOf<CosmeticKindDeclarationException>().With.Message.Contains("shares layer"));

    [Test]
    public void Two_kinds_cannot_derive_one_feature_flag()
        => Assert.That(() => CosmeticKindRegistry.FromTypes([typeof(DottedKey), typeof(DashedKey)]),
            Throws.TypeOf<CosmeticKindDeclarationException>().With.Message.Contains("already gates"));

    [Test]
    public void An_axis_over_a_kind_nobody_declared_is_refused()
        => Assert.That(() => CosmeticKindRegistry.FromTypes([typeof(AxisOverNothing)]),
            Throws.TypeOf<CosmeticKindDeclarationException>().With.Message.Contains("no kind file declares"));

    [Test]
    public void An_axis_over_a_kind_that_is_worn_is_refused()
        => Assert.That(() => CosmeticKindRegistry.FromTypes([typeof(Twin), typeof(AxisOverWorn)]),
            Throws.TypeOf<CosmeticKindDeclarationException>().With.Message.Contains("worn rather than chosen"));

    [Test]
    public void A_key_that_is_not_lowercase_dotted_is_refused()
        => Assert.That(() => CosmeticKindRegistry.Describe(typeof(ShoutingKey)),
            Throws.TypeOf<CosmeticKindDeclarationException>().With.Message.Contains("lowercase dotted"));

    [Test]
    public void A_kind_with_rows_and_no_payload_is_refused()
        => Assert.That(() => CosmeticKindRegistry.Describe(typeof(NoPayload)),
            Throws.TypeOf<CosmeticKindDeclarationException>().With.Message.Contains("declares no payload"));

    /// <summary>
    /// A payload describes a row, and a bare kind has none — so one would be a schema nothing is
    /// ever checked against, which is worse than no schema at all.
    /// </summary>
    [Test]
    public void A_bare_kind_with_a_payload_is_refused()
        => Assert.That(() => CosmeticKindRegistry.Describe(typeof(BarePayload)),
            Throws.TypeOf<CosmeticKindDeclarationException>().With.Message.Contains("is bare and also declares a payload"));

    [Test]
    public void An_ordered_kind_without_a_slot_ceiling_is_refused()
        => Assert.That(() => CosmeticKindRegistry.Describe(typeof(UnboundedStack)),
            Throws.TypeOf<CosmeticKindDeclarationException>().With.Message.Contains("no MaxSlots"));

    /// <summary>A kind whose key is gone is not found, and is not an exception either.</summary>
    [Test]
    public void An_orphaned_key_is_simply_absent()
    {
        var registry = Shipped();

        Assert.Multiple(() =>
        {
            Assert.That(registry.Contains("profile.background"), Is.False);
            Assert.That(registry.Find("profile.background"), Is.Null);
            Assert.That(registry.TryGet("profile.background", out _), Is.False);
        });
    }

    private sealed class Payload;

    private sealed class Twin : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.twin").Rendering(RenderPrimitive.ImageLayer)
                .On(CosmeticSurface.Avatar).Layer(1).Payload<Payload>();
    }

    private sealed class TwinAgain : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.twin").Rendering(RenderPrimitive.ImageLayer)
                .On(CosmeticSurface.Avatar).Layer(2).Payload<Payload>();
    }

    private sealed class Overlapping : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.overlapping").Rendering(RenderPrimitive.ImageLayer)
                .On(CosmeticSurface.Avatar).Layer(1).Payload<Payload>();
    }

    private sealed class DottedKey : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.a.b").Rendering(RenderPrimitive.ImageLayer)
                .On(CosmeticSurface.Avatar).Layer(10).Payload<Payload>();
    }

    private sealed class DashedKey : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.a-b").Rendering(RenderPrimitive.ImageLayer)
                .On(CosmeticSurface.Avatar).Layer(11).Payload<Payload>();
    }

    private sealed class AxisOverNothing : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.axis-over-nothing").Rendering(RenderPrimitive.TextStyle)
                .On(CosmeticSurface.ProfileCard).Layer(20).Payload<Payload>()
                .Facet("colour", "option.nothing");
    }

    private sealed class AxisOverWorn : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.axis-over-worn").Rendering(RenderPrimitive.TextStyle)
                .On(CosmeticSurface.ProfileCard).Layer(21).Payload<Payload>()
                .Facet("twin", "test.twin");
    }

    private sealed class ShoutingKey : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("Test.Shouting").Rendering(RenderPrimitive.ImageLayer)
                .On(CosmeticSurface.Avatar).Layer(30).Payload<Payload>();
    }

    private sealed class NoPayload : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.no-payload").Rendering(RenderPrimitive.ImageLayer)
                .On(CosmeticSurface.Avatar).Layer(31);
    }

    private sealed class BarePayload : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.bare-payload").Rendering(RenderPrimitive.TextStyle)
                .On(CosmeticSurface.ProfileCard).Layer(32).Bare().Payload<Payload>();
    }

    private sealed class UnboundedStack : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.unbounded").Rendering(RenderPrimitive.ImageLayer)
                .On(CosmeticSurface.Avatar).Layer(33).Stacking(StackingRule.Ordered).Payload<Payload>();
    }
}
