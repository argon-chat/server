namespace ArgonSharedLogicTest;

using Argon.Entities;
using Argon.Features.Cosmetics;
using Argon.Features.Cosmetics.Kinds;
using ArgonContracts;

/// <summary>
/// What a person's worn rows turn into on the wire, once the catalogue has had its say.
/// </summary>
/// <remarks>
/// <para>Two rules under test. Anything which no longer resolves is left out rather than drawn
/// half-way — and "left out" means the row stays in the table, so turning the thing back on restores
/// what people had on. And what goes out is references, never content: a profile is read by the
/// hundred, so a worn item is its id and nothing else.</para>
///
/// <para>No database: the projection takes rows already read, which is what lets one batch read serve
/// a whole member list.</para>
/// </remarks>
[TestFixture]
public class CosmeticWornProjectionTests
{
    private static readonly CosmeticKindRegistry Registry =
        CosmeticKindRegistry.FromAssembly(typeof(ICosmeticKind).Assembly);

    private static readonly Guid Wearer = Guid.NewGuid();

    private static HashSet<string> AllKinds() => Registry.All.Select(kind => kind.Key).ToHashSet();

    private static CosmeticItemEntity Item(string kindKey, string slug = "row")
        => new() { Id = Guid.NewGuid(), KindKey = kindKey, Slug = slug, NameKey = slug };

    private static CosmeticEquipEntity Worn(CosmeticItemEntity item)
        => new() { UserId = Wearer, KindKey = item.KindKey, SlotIndex = 0, CosmeticItemId = item.Id };

    private static CosmeticEquipEntity Nickname(Dictionary<string, Guid>? choices = null, string? tuning = null, Guid? itemId = null)
        => new()
        {
            UserId         = Wearer,
            KindKey        = NicknameStyleKind.Key,
            SlotIndex      = 0,
            CosmeticItemId = itemId,
            Choices        = choices is null ? null : Newtonsoft.Json.JsonConvert.SerializeObject(choices),
            Tuning         = tuning
        };

    private static Dictionary<Guid, CosmeticItemEntity> Servable(params CosmeticItemEntity[] items)
        => items.ToDictionary(item => item.Id);

    [Test]
    public void A_worn_item_goes_out_as_its_id_and_nothing_else()
    {
        var halo = Item("avatar.decoration");

        var worn = CosmeticWornProjection.Project([Worn(halo)], Servable(halo), Registry, AllKinds());

        Assert.That(worn.Single(), Is.EqualTo(new WornItem(halo.Id)));
    }

    /// <summary>Unpublished, switched off or out of its window: the read left it out of the servable set.</summary>
    [Test]
    public void An_item_that_may_not_be_served_is_left_out()
        => Assert.That(CosmeticWornProjection.Project([Worn(Item("avatar.decoration"))], Servable(), Registry, AllKinds()), Is.Empty);

    [Test]
    public void A_kind_that_is_switched_off_is_left_out()
    {
        var halo    = Item("avatar.decoration");
        var enabled = AllKinds();

        enabled.Remove("avatar.decoration");

        Assert.That(CosmeticWornProjection.Project([Worn(halo)], Servable(halo), Registry, enabled), Is.Empty);
    }

    /// <summary>A kind whose file was deleted from the build: its rows are orphans, never served.</summary>
    [Test]
    public void A_kind_this_build_does_not_declare_is_left_out()
    {
        var old = Item("profile.background");

        Assert.That(CosmeticWornProjection.Project([Worn(old)], Servable(old), Registry, AllKinds()), Is.Empty);
    }

    [Test]
    public void A_name_goes_out_as_its_options_and_its_numbers()
    {
        var font   = Item("option.font");
        var effect = Item("option.text-effect");

        var row = Nickname(
            new Dictionary<string, Guid> { [NicknameStyleKind.FontAxis] = font.Id, [NicknameStyleKind.EffectAxis] = effect.Id },
            """{"colors":[-32944,-16711936],"shape":"conic","weight":700}""");

        var worn = CosmeticWornProjection.Project([row], Servable(font, effect), Registry, AllKinds());

        Assert.That(worn.Single(), Is.InstanceOf<WornNickname>());

        var nickname = (WornNickname)worn.Single();

        Assert.Multiple(() =>
        {
            Assert.That(nickname.font, Is.EqualTo(font.Id));
            Assert.That(nickname.effect, Is.EqualTo(effect.Id));
            Assert.That(nickname.colors, Is.EqualTo(new[] { -32944, -16711936 }));
            Assert.That(nickname.shape, Is.EqualTo(NicknameGradientShape.Conic));
            Assert.That(nickname.weight, Is.EqualTo((ushort)700));
        });
    }

    /// <summary>A name keeps its treatment when the face it was set in goes away.</summary>
    [Test]
    public void An_option_that_no_longer_resolves_drops_only_its_axis()
    {
        var retired = Item("option.font");
        var effect  = Item("option.text-effect");

        var row = Nickname(new Dictionary<string, Guid>
        {
            [NicknameStyleKind.FontAxis]   = retired.Id,
            [NicknameStyleKind.EffectAxis] = effect.Id
        });

        var nickname = (WornNickname)CosmeticWornProjection.Project([row], Servable(effect), Registry, AllKinds()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(nickname.font, Is.Null);
            Assert.That(nickname.effect, Is.EqualTo(effect.Id));
        });
    }

    /// <summary>An option row that is some other axis's kind is not this axis's option.</summary>
    [Test]
    public void An_option_of_the_wrong_kind_is_not_drawn_on_the_axis()
    {
        var effect = Item("option.text-effect");
        var row    = Nickname(new Dictionary<string, Guid> { [NicknameStyleKind.FontAxis] = effect.Id });

        Assert.That(CosmeticWornProjection.Project([row], Servable(effect), Registry, AllKinds()), Is.Empty);
    }

    [Test]
    public void A_name_with_nothing_left_to_say_is_not_drawn()
    {
        var row = Nickname(new Dictionary<string, Guid> { [NicknameStyleKind.FontAxis] = Guid.NewGuid() });

        Assert.That(CosmeticWornProjection.Project([row], Servable(), Registry, AllKinds()), Is.Empty);
    }

    /// <summary>A bare kind has no row to point at; one that does was written by something else.</summary>
    [Test]
    public void A_bare_kind_pointing_at_an_item_is_not_drawn()
    {
        var stray = Item(NicknameStyleKind.Key);

        Assert.That(CosmeticWornProjection.Project([Nickname(tuning: """{"weight":700}""", itemId: stray.Id)],
            Servable(stray), Registry, AllKinds()), Is.Empty);
    }

    [Test]
    public void Cosmetics_arrive_in_compositing_order()
    {
        var frame = Item("profile.frame");
        var halo  = Item("avatar.decoration");

        var worn = CosmeticWornProjection.Project(
            [Worn(frame), Nickname(tuning: """{"weight":700}"""), Worn(halo)], Servable(frame, halo), Registry, AllKinds());

        Assert.That(worn.Select(cosmetic => cosmetic.GetType()),
            Is.EqualTo(new[] { typeof(WornItem), typeof(WornNickname), typeof(WornItem) }));

        Assert.That(((WornItem)worn[0]).itemId, Is.EqualTo(halo.Id));
    }
}
