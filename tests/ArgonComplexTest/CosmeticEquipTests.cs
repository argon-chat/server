namespace ArgonComplexTest.Tests;

using Argon;
using Argon.Entities;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Putting cosmetics on, taking them off, and everybody else seeing it.
/// </summary>
/// <remarks>
/// <para>The part a unit test cannot reach is the last one. What somebody wears is read through a
/// cache shared by every reader, so a change that does not drop the wearer's entry is a change only
/// the wearer ever sees — and the wearer is exactly the person who would never notice, because their
/// own client redraws from the equip answer. So every change here is checked from a second account
/// reading the member list.</para>
///
/// <para>The catalogue is written straight into the database, as it will be until the admin console
/// grows the screens for it.</para>
/// </remarks>
[TestFixture]
public class CosmeticEquipTests : TestBase
{
    private async Task<CosmeticItemEntity> PublishAsync(
        string kindKey,
        string slug,
        string payload,
        CosmeticAcquisitionMode acquisition = CosmeticAcquisitionMode.Free,
        bool published = true,
        CancellationToken ct = default)
    {
        await using var ctx = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        var item = new CosmeticItemEntity
        {
            Id              = Guid.NewGuid(),
            KindKey         = kindKey,
            Slug            = $"{slug}-{Guid.NewGuid():N}",
            NameKey         = slug,
            Payload         = payload,
            AcquisitionMode = acquisition,
            IsPublished     = published,
            PublishedAt     = published ? DateTimeOffset.UtcNow : null
        };

        ctx.Cosmetics.Add(item);
        await ctx.SaveChangesAsync(ct);

        return item;
    }

    private async Task GrantAsync(Guid userId, Guid cosmeticId, CancellationToken ct)
    {
        await using var ctx = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        ctx.CosmeticOwnerships.Add(new CosmeticOwnershipEntity
        {
            Id             = Guid.NewGuid(),
            UserId         = userId,
            CosmeticItemId = cosmeticId
        });

        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>A space with the wearer in it and somebody else to look at them.</summary>
    private async Task<(TestUserSession Wearer, TestUserSession Onlooker, Guid SpaceId)> TwoInASpaceAsync(CancellationToken ct)
    {
        var wearer   = await CreateSessionAsync(ct);
        var onlooker = await CreateSessionAsync(ct);

        var created = await wearer.Users.CreateSpace(new CreateServerRequest("Cosmetics", "Description", string.Empty), ct);

        Assert.That(created, Is.InstanceOf<SuccessCreateSpace>());

        var spaceId = ((SuccessCreateSpace)created).space.spaceId;
        var code    = await wearer.Servers.CreateInviteCode(spaceId, 60, 0, ct);

        Assert.That(await onlooker.Users.JoinToSpace(code, ct), Is.InstanceOf<SuccessJoin>());

        return (wearer, onlooker, spaceId);
    }

    private static async Task<IonArray<IWornCosmetic>> SeenBy(TestUserSession onlooker, Guid spaceId, Guid wearer, CancellationToken ct)
    {
        var profiles = await onlooker.Servers.PrefetchProfiles(spaceId, new IonArray<Guid>([wearer]), ct);

        return WornOn(profiles.Values[0]);
    }

    /// <summary>The catalogue rows worn as items, in the order they arrived.</summary>
    private static Guid[] Items(IonArray<IWornCosmetic> worn) => worn.OfType<WornItem>().Select(item => item.itemId).ToArray();

    /// <summary>What a profile says is worn. Null would be a server that did not say, which is a failure here.</summary>
    private static IonArray<IWornCosmetic> WornOn(ArgonUserProfile profile)
    {
        Assert.That(profile.cosmetics, Is.Not.Null, "the profile came back without saying what is worn");

        return profile.cosmetics!.Value;
    }

    [Test, CancelAfter(120_000)]
    public async Task The_catalogue_lists_what_is_published_and_nothing_else(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var published = await PublishAsync("avatar.decoration", "halo", """{"insetPct":10,"beneath":false}""", ct: ct);
        var draft     = await PublishAsync("avatar.decoration", "draft", """{"insetPct":0,"beneath":false}""", published: false, ct: ct);

        var catalogue = await session.Cosmetics.GetCatalogue(ct);
        var ids       = catalogue.items.Select(item => item.cosmeticId).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(ids, Does.Contain(published.Id));
            Assert.That(ids, Does.Not.Contain(draft.Id));
            Assert.That(catalogue.enabledKinds, Does.Contain("nickname.style"));

            // Typed, never a document: Ion carries no JSON.
            Assert.That(catalogue.items.Single(item => item.cosmeticId == published.Id).payload,
                Is.EqualTo(new PayloadAvatarDecoration(10, false)));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task What_is_put_on_is_seen_by_everybody_in_the_space(CancellationToken ct = default)
    {
        var (wearer, onlooker, spaceId) = await TwoInASpaceAsync(ct);
        var halo = await PublishAsync("avatar.decoration", "halo", """{"insetPct":10,"beneath":false}""", ct: ct);

        // Read once before, so the onlooker's answer is in the cache — the equip has to drop it.
        Assert.That(await SeenBy(onlooker, spaceId, wearer.UserId, ct), Is.Empty);

        var equipped = await wearer.Cosmetics.Equip(new WornItem(halo.Id), ct);

        Assert.That(equipped, Is.InstanceOf<SuccessEquip>(), $"{(equipped as FailedEquip)?.error}");
        Assert.That(Items(WornOn(((SuccessEquip)equipped).profile)), Is.EqualTo(new[] { halo.Id }));

        // A reference and nothing else: what the row looks like is the catalogue's to say.
        Assert.That(await SeenBy(onlooker, spaceId, wearer.UserId, ct), Is.EqualTo(new IWornCosmetic[] { new WornItem(halo.Id) }));

        Assert.That(Items(WornOn(await wearer.Users.GetMyProfile(ct))), Is.EqualTo(new[] { halo.Id }));
    }

    [Test, CancelAfter(120_000)]
    public async Task What_is_taken_off_is_gone_for_everybody(CancellationToken ct = default)
    {
        var (wearer, onlooker, spaceId) = await TwoInASpaceAsync(ct);
        var halo = await PublishAsync("avatar.decoration", "halo", """{"insetPct":10,"beneath":false}""", ct: ct);

        Assert.That(await wearer.Cosmetics.Equip(new WornItem(halo.Id), ct), Is.InstanceOf<SuccessEquip>());
        Assert.That(await SeenBy(onlooker, spaceId, wearer.UserId, ct), Has.Count.EqualTo(1));

        Assert.That(await wearer.Cosmetics.Unequip("avatar.decoration", ct), Is.InstanceOf<SuccessEquip>());
        Assert.That(await SeenBy(onlooker, spaceId, wearer.UserId, ct), Is.Empty);

        var again = await wearer.Cosmetics.Unequip("avatar.decoration", ct);

        Assert.That((again as FailedEquip)?.error, Is.EqualTo(CosmeticError.NOT_FOUND));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_granted_item_needs_the_grant(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var frame = await PublishAsync("profile.frame", "gold",
            """{"parts":[{"type":"ring","thickness":2,"colors":[-10496]}]}""",
            CosmeticAcquisitionMode.OperatorGrant, ct: ct);

        var refused = await session.Cosmetics.Equip(new WornItem(frame.Id), ct);

        Assert.That((refused as FailedEquip)?.error, Is.EqualTo(CosmeticError.NOT_OWNED));

        await GrantAsync(session.UserId, frame.Id, ct);

        Assert.That(await session.Cosmetics.Equip(new WornItem(frame.Id), ct), Is.InstanceOf<SuccessEquip>());
        Assert.That((await session.Cosmetics.GetMyCosmetics(ct)).owned.Select(o => o.cosmeticId), Does.Contain(frame.Id));
    }

    [Test, CancelAfter(120_000)]
    public async Task What_cannot_be_worn_is_refused_for_the_right_reason(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var draft = await PublishAsync("avatar.decoration", "draft", """{"insetPct":0,"beneath":false}""", published: false, ct: ct);
        var face  = await PublishAsync("option.font", "inter", """{"cssFamily":"Inter"}""", ct: ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(((FailedEquip)await session.Cosmetics.Equip(new WornItem(Guid.NewGuid()), ct)).error, Is.EqualTo(CosmeticError.NOT_FOUND));
            Assert.That(((FailedEquip)await session.Cosmetics.Equip(new WornItem(draft.Id), ct)).error, Is.EqualTo(CosmeticError.ITEM_UNAVAILABLE));

            // An option is only ever chosen on somebody's axis, never worn on its own.
            Assert.That(((FailedEquip)await session.Cosmetics.Equip(new WornItem(face.Id), ct)).error, Is.EqualTo(CosmeticError.ITEM_UNAVAILABLE));

            Assert.That(((FailedEquip)await session.Cosmetics.Unequip("profile.background", ct)).error, Is.EqualTo(CosmeticError.UNKNOWN_KIND));
        });
    }

    private static WornNickname Look(Guid? font = null, Guid? effect = null, int[]? colors = null, ushort? weight = null, int? letterSpacing = null)
        => new(font, effect, colors is null ? (IonArray<int>?)null : new IonArray<int>(colors), null, null, null, weight, letterSpacing);

    [Test, CancelAfter(120_000)]
    public async Task A_name_is_put_on_by_choosing_its_look(CancellationToken ct = default)
    {
        var (wearer, onlooker, spaceId) = await TwoInASpaceAsync(ct);
        var face = await PublishAsync("option.font", "inter", """{"cssFamily":"Inter"}""", ct: ct);

        var styled = await wearer.Cosmetics.Equip(Look(font: face.Id, colors: [-32944, -16711936], weight: 700), ct);

        Assert.That(styled, Is.InstanceOf<SuccessEquip>(), $"{(styled as FailedEquip)?.error}");

        var seen = await SeenBy(onlooker, spaceId, wearer.UserId, ct);

        Assert.That(seen.Single(), Is.InstanceOf<WornNickname>());

        var nickname = (WornNickname)seen.Single();

        Assert.Multiple(() =>
        {
            Assert.That(nickname.font, Is.EqualTo(face.Id));
            Assert.That(nickname.effect, Is.Null);
            Assert.That(nickname.colors, Is.EqualTo(new[] { -32944, -16711936 }));
            Assert.That(nickname.weight, Is.EqualTo((ushort)700));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_name_with_nothing_chosen_is_a_name_not_worn(CancellationToken ct = default)
    {
        var (wearer, onlooker, spaceId) = await TwoInASpaceAsync(ct);

        Assert.That(await wearer.Cosmetics.Equip(Look(weight: 700), ct), Is.InstanceOf<SuccessEquip>());
        Assert.That(await SeenBy(onlooker, spaceId, wearer.UserId, ct), Has.Count.EqualTo(1));

        Assert.That(await wearer.Cosmetics.Equip(Look(), ct), Is.InstanceOf<SuccessEquip>());
        Assert.That(await SeenBy(onlooker, spaceId, wearer.UserId, ct), Is.Empty);
    }

    [Test, CancelAfter(120_000)]
    public async Task A_look_that_does_not_fit_is_refused(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var draft = await PublishAsync("option.font", "draft-face", """{"cssFamily":"Draft"}""", published: false, ct: ct);
        var glow  = await PublishAsync("option.text-effect", "glow", "{}", ct: ct);

        await Assert.MultipleAsync(async () =>
        {
            // An option that is not published.
            Assert.That(((FailedEquip)await session.Cosmetics.Equip(Look(font: draft.Id), ct)).error,
                Is.EqualTo(CosmeticError.CHOICE_INVALID));

            // An option of another axis's kind: a treatment offered as a face.
            Assert.That(((FailedEquip)await session.Cosmetics.Equip(Look(font: glow.Id), ct)).error,
                Is.EqualTo(CosmeticError.CHOICE_INVALID));

            // A seventh colour, which the look has no room for.
            Assert.That(((FailedEquip)await session.Cosmetics.Equip(Look(colors: [1, 2, 3, 4, 5, 6, 7]), ct)).error,
                Is.EqualTo(CosmeticError.VALUE_OUT_OF_RANGE));

            // A weight that is not a weight.
            Assert.That(((FailedEquip)await session.Cosmetics.Equip(Look(weight: 1000), ct)).error,
                Is.EqualTo(CosmeticError.VALUE_OUT_OF_RANGE));
        });
    }
}
