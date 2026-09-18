namespace ArgonSharedLogicTest;

using System.ComponentModel.DataAnnotations;
using Argon.Entities;
using Argon.Features.Cosmetics;
using Argon.Features.Cosmetics.Kinds;

/// <summary>
/// The cosmetics kind registry: what the build ships, and what it refuses to ship.
/// </summary>
/// <remarks>
/// <para>The registry is the whole of "does this kind exist" — a file under <c>Features/Cosmetics/Kinds</c>
/// is a feature and deleting it removes one. That makes two things worth pinning. The first is that
/// discovery actually finds them, because a kind that is declared and not discovered is a feature
/// that silently does not exist. The second is the set of declarations the registry refuses: two
/// kinds on one layer of one surface, or two claiming the same pre-cosmetics field, have no
/// deterministic answer at render time, and the one that won would depend on the order reflection
/// happened to return types in.</para>
///
/// <para>The refusals are asserted through purpose-built kinds declared in this file rather than by
/// breaking a real one. They live in the test assembly, so <c>FromAssembly</c> over the production
/// assembly never sees them.</para>
/// </remarks>
[TestFixture]
public class CosmeticKindRegistryTests
{
    private static CosmeticKindRegistry Production()
        => CosmeticKindRegistry.FromAssembly(typeof(ICosmeticKind).Assembly);

    [Test]
    public void Every_kind_file_in_the_build_is_discovered()
    {
        var keys = Production().All.Select(definition => definition.Key).ToArray();

        Assert.That(keys, Is.EquivalentTo(new[]
        {
            "profile.background", "profile.badge", "profile.frame", "profile.effect", "profile.scene",
            "avatar.decoration", "avatar.orbit", "nickname.style",
            "widget.note", "widget.tags", "widget.picture",
            "option.font", "option.swatch", "option.text-effect"
        }));
    }

    /// <summary>
    /// The flag key the client's prefix passthrough looks for, derived rather than typed twice.
    /// </summary>
    /// <remarks>
    /// Both halves matter. The <c>af.</c> prefix is what every other flag in the product uses and
    /// what <c>featureFlagsStore</c> filters on; the dots becoming dashes is what keeps one kind's
    /// flag from reading as a hierarchy the flag system does not have.
    /// </remarks>
    [Test]
    public void A_kinds_feature_flag_follows_from_its_key()
    {
        var background = Production().Find("profile.background");

        Assert.That(background, Is.Not.Null);
        Assert.That(background!.FeatureFlagKey, Is.EqualTo("af.cosmetics.profile-background.active"));
    }

    [Test]
    public void A_kind_renders_only_on_the_surfaces_it_names()
    {
        var decoration = Production().Find("avatar.decoration")!;

        Assert.Multiple(() =>
        {
            Assert.That(decoration.RendersOn(CosmeticSurface.Avatar), Is.True);
            Assert.That(decoration.RendersOn(CosmeticSurface.ProfileCard), Is.False);
        });
    }

    [Test]
    public void An_unknown_key_is_absent_rather_than_a_throw()
    {
        // This is the deleted-file path: rows keep a key nobody declares any more, and every read
        // goes through the lookup first. A throw here would turn a retired kind into an outage.
        var registry = Production();

        Assert.Multiple(() =>
        {
            Assert.That(registry.Contains("profile.aurora"), Is.False);
            Assert.That(registry.Find("profile.aurora"), Is.Null);
            Assert.That(registry.TryGet("profile.aurora", out _), Is.False);
        });
    }

    [Test]
    public void Two_kinds_cannot_claim_one_key()
    {
        var error = Assert.Throws<CosmeticKindDeclarationException>(
            () => CosmeticKindRegistry.FromTypes([typeof(DuplicateKeyKind), typeof(ProfileBackgroundKind)]));

        Assert.That(error!.Message, Does.Contain("profile.background"));
    }

    [Test]
    public void Two_kinds_cannot_share_a_layer_on_one_surface()
    {
        var error = Assert.Throws<CosmeticKindDeclarationException>(
            () => CosmeticKindRegistry.FromTypes([typeof(LayerOneKind), typeof(LayerOneAgainKind)]));

        Assert.That(error!.Message, Does.Contain("shares layer 1 on Banner"));
    }

    [Test]
    public void Two_kinds_cannot_project_onto_one_legacy_field()
    {
        var error = Assert.Throws<CosmeticKindDeclarationException>(
            () => CosmeticKindRegistry.FromTypes([typeof(LegacyClaimKind), typeof(ProfileBackgroundKind)]));

        Assert.That(error!.Message, Does.Contain("BackgroundId"));
    }

    /// <summary>
    /// Badges are the one legacy field several kinds may feed, because the field is an array.
    /// </summary>
    [Test]
    public void Several_kinds_may_feed_the_legacy_badge_array()
    {
        var registry = CosmeticKindRegistry.FromTypes([typeof(BadgeKind), typeof(SecondBadgeKind)]);

        Assert.That(registry.ForLegacyBadges().Count(), Is.EqualTo(2));
    }

    /// <summary>
    /// Two keys that differ only in where a separator falls derive one flag, and one kind would
    /// gate the other from a console showing a single row.
    /// </summary>
    [Test]
    public void Two_kinds_cannot_derive_one_feature_flag()
    {
        var error = Assert.Throws<CosmeticKindDeclarationException>(
            () => CosmeticKindRegistry.FromTypes([typeof(DashedKind), typeof(DottedKind)]));

        Assert.That(error!.Message, Does.Contain("af.cosmetics.test-member-list.active"));
    }

    [Test]
    public void A_key_that_is_not_dotted_lowercase_is_refused()
        => Assert.Throws<CosmeticKindDeclarationException>(() => CosmeticKindRegistry.Describe(typeof(ShoutingKeyKind)));

    [Test]
    public void An_ordered_kind_must_bound_its_slots()
        => Assert.Throws<CosmeticKindDeclarationException>(() => CosmeticKindRegistry.Describe(typeof(UnboundedKind)));

    [Test]
    public void A_single_kind_cannot_ask_for_several_slots()
        => Assert.Throws<CosmeticKindDeclarationException>(() => CosmeticKindRegistry.Describe(typeof(ConfusedStackingKind)));

    [Test]
    public void A_kind_that_names_no_payload_is_refused()
        => Assert.Throws<CosmeticKindDeclarationException>(() => CosmeticKindRegistry.Describe(typeof(PayloadlessKind)));

    /// <summary>
    /// An axis has to name a kind that exists and is one whose items are options.
    /// </summary>
    /// <remarks>
    /// Checked at build rather than at equip, because an axis pointing at nothing is a picker that is
    /// permanently empty — which reads as "the operator has published nothing" and is indistinguishable
    /// from it right up until somebody goes looking.
    /// </remarks>
    [Test]
    public void Every_axis_names_a_compositional_kind_that_exists()
    {
        var registry = Production();

        Assert.Multiple(() =>
        {
            foreach (var kind in registry.All)
            {
                foreach (var facet in kind.Facets.Values)
                {
                    var option = registry.Find(facet.OptionKindKey);

                    Assert.That(option, Is.Not.Null, $"{kind.Key}.{facet.Id} names '{facet.OptionKindKey}', which no file declares");
                    Assert.That(option!.IsCompositional, Is.True, $"{kind.Key}.{facet.Id} names '{facet.OptionKindKey}', which is worn rather than chosen");
                }
            }
        });
    }

    /// <summary>
    /// A board card has a schema for its wearer and renders as a widget, and the two go together:
    /// content with no widget slot would be written and never drawn.
    /// </summary>
    [Test]
    public void Every_board_card_declares_content_and_renders_as_one()
    {
        var registry = Production();

        Assert.Multiple(() =>
        {
            foreach (var kind in registry.All)
            {
                if (kind.Board is null)
                {
                    Assert.That(kind.Primitive, Is.Not.EqualTo(RenderPrimitive.WidgetSlot),
                        $"{kind.Key} renders as a board card but declares nothing for its wearer to fill in");
                    continue;
                }

                Assert.That(kind.Primitive, Is.EqualTo(RenderPrimitive.WidgetSlot), kind.Key);
                Assert.That(kind.Board.ContentType, Is.Not.Null, kind.Key);
                Assert.That(kind.Board.MaxWidth, Is.InRange(1, CosmeticContent.Columns), kind.Key);
                Assert.That(kind.Board.MinHeight, Is.GreaterThanOrEqualTo(1), kind.Key);
                Assert.That(kind.Board.MaxHeight, Is.GreaterThanOrEqualTo(kind.Board.MinHeight), kind.Key);
            }
        });
    }

    /// <summary>
    /// An operator's decisions about a card are held inside what its code can draw.
    /// </summary>
    /// <remarks>
    /// The two are different things and this is the seam between them: how wide a card <i>can</i> be
    /// is its component's business, and how many of it are <i>offered</i> is an operator's. A row that
    /// could raise its own ceiling would be a picker offering something the write path then refuses.
    /// </remarks>
    [Test]
    public void A_rows_offer_cannot_exceed_what_its_kind_can_draw()
    {
        var kind = CosmeticKindRegistry.Describe(typeof(NoteWidgetKind));

        var greedy = new CosmeticItemEntity
        {
            Id            = Guid.NewGuid(),
            KindKey       = "widget.note",
            Slug          = "note",
            NameKey       = "cosmetic_widget_note",
            Payload       = "{}",
            AssetFileIds  = new Dictionary<string, string>(),
            MaxPerBoard   = 99,
            BoardDefaultW = 99,
            BoardDefaultH = 99
        };

        var offer = CosmeticBoardOffer.Resolve(kind, greedy);

        Assert.That(offer, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(offer!.Value.MaxPerBoard, Is.EqualTo(kind.MaxSlots));
            Assert.That(offer.Value.DefaultWidth, Is.EqualTo(Math.Min(CosmeticContent.Columns, kind.Board!.MaxWidth)));
            Assert.That(offer.Value.DefaultHeight, Is.EqualTo(kind.Board.MaxHeight));
        });
    }

    /// <summary>An unset decision falls through to the kind rather than to a number in this file.</summary>
    [Test]
    public void A_row_that_decides_nothing_takes_its_kinds_own_limits()
    {
        var kind = CosmeticKindRegistry.Describe(typeof(NoteWidgetKind));

        var plain = new CosmeticItemEntity
        {
            Id           = Guid.NewGuid(),
            KindKey      = "widget.note",
            Slug         = "note",
            NameKey      = "cosmetic_widget_note",
            Payload      = "{}",
            AssetFileIds = new Dictionary<string, string>()
        };

        var offer = CosmeticBoardOffer.Resolve(kind, plain)!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(offer.MaxPerBoard, Is.EqualTo(kind.MaxSlots));
            Assert.That(offer.DefaultWidth, Is.EqualTo(kind.Board!.MinWidth));
            Assert.That(offer.DefaultHeight, Is.EqualTo(kind.Board.MinHeight));
        });
    }

    /// <summary>
    /// A card dropped past the bottom of the board comes back inside it, whole.
    /// </summary>
    /// <remarks>
    /// The height counts, not just the top edge: a profile is as tall as the lowest card on it, so a
    /// card hanging off the bottom by its own length stretches the card of everybody reading it.
    /// </remarks>
    [Test]
    public void A_card_dragged_past_the_bottom_lands_inside_the_board()
    {
        var kind = CosmeticKindRegistry.Describe(typeof(NoteWidgetKind));

        var (x, y, w, h) = CosmeticContent.ClampCell(kind, x: 99, y: 9999, w: 99, h: 99);

        Assert.Multiple(() =>
        {
            Assert.That(x + w, Is.LessThanOrEqualTo(CosmeticContent.Columns));
            Assert.That(y + h, Is.LessThanOrEqualTo(CosmeticContent.Rows));
            Assert.That(h, Is.EqualTo(kind.Board!.MaxHeight));
        });
    }

    /// <summary>Nothing that is not a board card has anything to offer on one.</summary>
    [Test]
    public void A_kind_that_draws_no_card_has_no_offer()
    {
        var kind = CosmeticKindRegistry.Describe(typeof(BadgeKind));

        var badge = new CosmeticItemEntity
        {
            Id           = Guid.NewGuid(),
            KindKey      = "profile.badge",
            Slug         = "owner",
            NameKey      = "cosmetic_badge_owner",
            Payload      = """{"tooltipKey":"badge_owner"}""",
            AssetFileIds = new Dictionary<string, string>(),
            MaxPerBoard  = 3
        };

        Assert.That(CosmeticBoardOffer.Resolve(kind, badge), Is.Null);
    }

    /// <summary>
    /// A compositional kind is kept out of the wardrobe by having nowhere to appear, and that is the
    /// only thing keeping it out — so the two have to stay in step.
    /// </summary>
    [Test]
    public void A_compositional_kind_has_no_surface()
    {
        foreach (var kind in Production().All)
        {
            if (kind.IsCompositional)
                Assert.That(kind.Surfaces, Is.EqualTo(CosmeticSurface.None), kind.Key);
        }
    }

    [Test]
    public void A_required_asset_is_reported_as_one()
    {
        var background = Production().Find("profile.background")!;

        Assert.Multiple(() =>
        {
            Assert.That(background.RequiredAssets().Select(asset => asset.Slot),
                Is.EquivalentTo(new[] { CosmeticAssetSlot.Primary }));
            Assert.That(background.Assets[CosmeticAssetSlot.Poster].IsRequired, Is.False);
        });
    }

    private sealed class DuplicateKeyKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("profile.background")
               .Rendering(RenderPrimitive.ImageLayer)
               .On(CosmeticSurface.Banner)
               .Payload<EmptyPayload>();
    }

    private sealed class LayerOneKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.layer-one")
               .Rendering(RenderPrimitive.ImageLayer)
               .On(CosmeticSurface.Banner)
               .Layer(1)
               .Payload<EmptyPayload>();
    }

    private sealed class LayerOneAgainKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.layer-one-again")
               .Rendering(RenderPrimitive.ImageLayer)
               .On(CosmeticSurface.Banner)
               .Layer(1)
               .Payload<EmptyPayload>();
    }

    private sealed class LegacyClaimKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.other-background")
               .Rendering(RenderPrimitive.ImageLayer)
               .On(CosmeticSurface.Banner)
               .Layer(9001)
               .Payload<EmptyPayload>()
               .ProjectsToLegacy(LegacyCosmeticField.BackgroundId);
    }

    private sealed class SecondBadgeKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.second-badge")
               .Rendering(RenderPrimitive.IconBadge)
               .On(CosmeticSurface.Status)
               .Layer(310)
               .Payload<EmptyPayload>()
               .ProjectsToLegacy(LegacyCosmeticField.Badges);
    }

    private sealed class DashedKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.member-list")
               .Rendering(RenderPrimitive.ImageLayer)
               .On(CosmeticSurface.MemberListRow)
               .Layer(11)
               .Payload<EmptyPayload>();
    }

    private sealed class DottedKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.member.list")
               .Rendering(RenderPrimitive.ImageLayer)
               .On(CosmeticSurface.MemberListRow)
               .Layer(12)
               .Payload<EmptyPayload>();
    }

    private sealed class ShoutingKeyKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("Profile.Background")
               .Rendering(RenderPrimitive.ImageLayer)
               .On(CosmeticSurface.Banner)
               .Payload<EmptyPayload>();
    }

    private sealed class UnboundedKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.unbounded")
               .Rendering(RenderPrimitive.IconBadge)
               .On(CosmeticSurface.Banner)
               .Stacking(StackingRule.Ordered)
               .Payload<EmptyPayload>();
    }

    private sealed class ConfusedStackingKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.confused")
               .Rendering(RenderPrimitive.IconBadge)
               .On(CosmeticSurface.Banner)
               .Stacking(StackingRule.Single)
               .MaxSlots(4)
               .Payload<EmptyPayload>();
    }

    private sealed class PayloadlessKind : ICosmeticKind
    {
        public static void Describe(ICosmeticKindDescriptor d)
            => d.Keyed("test.payloadless")
               .Rendering(RenderPrimitive.ImageLayer)
               .On(CosmeticSurface.Banner);
    }

    private sealed class EmptyPayload
    {
        [Range(0, 10)]
        public int Weight { get; set; }
    }
}
