namespace ArgonSharedLogicTest;

using Argon.Entities;
using Argon.Features.Cosmetics;

/// <summary>
/// Who may have what on — the one rule equip, an axis choice and the post-lapse sweep all ask.
/// </summary>
[TestFixture]
public class CosmeticWearTests
{
    private static readonly CosmeticKindRegistry Registry =
        CosmeticKindRegistry.FromAssembly(typeof(ICosmeticKind).Assembly);

    private static CosmeticKindDefinition Frame => Registry.Find("profile.frame")!;

    private static CosmeticItemEntity Item(CosmeticAcquisitionMode mode)
        => new() { KindKey = "profile.frame", Slug = "vines", NameKey = "vines", AcquisitionMode = mode };

    [Test]
    public void A_free_item_is_everybody_s()
        => Assert.That(CosmeticWear.MayWear(Item(CosmeticAcquisitionMode.Free), Frame, hasPremium: false, holdsGrant: false), Is.True);

    [Test]
    public void A_grant_is_enough_on_its_own()
        => Assert.That(CosmeticWear.MayWear(Item(CosmeticAcquisitionMode.OperatorGrant), Frame, hasPremium: false, holdsGrant: true), Is.True);

    [Test]
    public void Without_a_grant_or_a_subscription_it_is_nobody_s()
        => Assert.That(CosmeticWear.MayWear(Item(CosmeticAcquisitionMode.OperatorGrant), Frame, hasPremium: false, holdsGrant: false), Is.False);

    [Test]
    public void A_subscription_covers_only_what_is_marked_for_it()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CosmeticWear.MayWear(Item(CosmeticAcquisitionMode.UltimaTier), Frame, hasPremium: true, holdsGrant: false), Is.True);
            Assert.That(CosmeticWear.MayWear(Item(CosmeticAcquisitionMode.OperatorGrant), Frame, hasPremium: true, holdsGrant: false), Is.False);
            Assert.That(CosmeticWear.MayWear(Item(CosmeticAcquisitionMode.UltimaTier), Frame, hasPremium: false, holdsGrant: false), Is.False);
        });
    }
}
