namespace ArgonComplexTest.Tests;

using Argon;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using Argon.Services.L1L2;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
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
///
/// <para>Every test that puts something on lives in this one fixture, because one of them switches
/// kinds off — and a kind is switched off for the whole server. Tests inside a fixture run one at a
/// time, so nothing here can be caught equipping while a kind is off.</para>
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
        Dictionary<string, string>? assets = null,
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
            PublishedAt     = published ? DateTimeOffset.UtcNow : null,
            AssetFileIds    = assets ?? new()
        };

        ctx.Cosmetics.Add(item);
        await ctx.SaveChangesAsync(ct);

        // The catalogue is cached in Redis as well, and a reused container keeps Redis between runs:
        // a row written behind the cache's back would otherwise stay unlisted until the entry expired.
        await FactoryAsp.Services.GetRequiredService<HybridCache>().RemoveAsync(ICosmeticsCache.CatalogueKey, ct);

        return item;
    }

    private async Task GrantAsync(Guid userId, Guid cosmeticId, CancellationToken ct, DateTimeOffset? expiresAt = null)
    {
        await using var ctx = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        ctx.CosmeticOwnerships.Add(new CosmeticOwnershipEntity
        {
            Id             = Guid.NewGuid(),
            UserId         = userId,
            CosmeticItemId = cosmeticId,
            ExpiresAt      = expiresAt
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
        var code    = await wearer.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();

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
    public async Task What_a_grant_allowed_comes_off_when_the_grant_runs_out(CancellationToken ct = default)
    {
        var (wearer, onlooker, spaceId) = await TwoInASpaceAsync(ct);
        var frame = await PublishAsync("profile.frame", "lapsing",
            """{"parts":[{"type":"ring","thickness":2,"colors":[-10496]}]}""",
            CosmeticAcquisitionMode.OperatorGrant, ct: ct);

        await GrantAsync(wearer.UserId, frame.Id, ct, expiresAt: DateTimeOffset.UtcNow.AddSeconds(10));

        Assert.That(await wearer.Cosmetics.Equip(new WornItem(frame.Id), ct), Is.InstanceOf<SuccessEquip>());
        Assert.That(Items(await SeenBy(onlooker, spaceId, wearer.UserId, ct)), Is.EqualTo(new[] { frame.Id }));

        // Nobody touches anything after this: what takes the frame off is the reminder the equip set.
        var seen = await Poll.ForValueAsync(() => SeenBy(onlooker, spaceId, wearer.UserId, ct),
            worn => worn.Count is 0, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), ct);

        Assert.That(Items(seen), Is.Empty, "the frame stayed on after the grant that allowed it ran out");
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

    private const string FramePayload = """{"parts":[{"type":"ring","thickness":2,"colors":[-10496]}]}""";
    private const string HaloPayload  = """{"insetPct":10,"beneath":false}""";
    private const string FontPayload  = """{"cssFamily":"Inter"}""";

    private Task SubscribeAsync(Guid userId, CancellationToken ct)
        => GetGrainFactory().GetGrain<IUltimaGrain>(userId).ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

    private Task EndSubscriptionAsync(Guid userId, CancellationToken ct)
        => GetGrainFactory().GetGrain<IUltimaGrain>(userId).ExpireSubscriptionAsync(ct);

    private static Task<IonArray<IWornCosmetic>> WornByAsync(TestUserSession wearer, Func<IonArray<IWornCosmetic>, bool> accept, CancellationToken ct)
        => Poll.ForValueAsync(async () => WornOn(await wearer.Users.GetMyProfile(ct)), accept,
            TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200), ct);

    // ── What a subscription covers ──────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task What_the_subscription_covers_is_owned_while_it_lasts(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var outsider   = await CreateSessionAsync(ct);

        var covered = await PublishAsync("profile.frame", "ultima-frame", FramePayload, CosmeticAcquisitionMode.UltimaTier, ct: ct);
        var granted = await PublishAsync("profile.frame", "granted-ultima-frame", FramePayload,
            CosmeticAcquisitionMode.UltimaTier | CosmeticAcquisitionMode.OperatorGrant, ct: ct);

        var grantEnds = DateTimeOffset.UtcNow.AddDays(3);
        await GrantAsync(subscriber.UserId, granted.Id, ct, expiresAt: grantEnds);
        await SubscribeAsync(subscriber.UserId, ct);

        var mine   = (await subscriber.Cosmetics.GetMyCosmetics(ct)).owned.ToList();
        var theirs = (await outsider.Cosmetics.GetMyCosmetics(ct)).owned.ToList();

        Assert.Multiple(() =>
        {
            var viaSubscription = mine.Single(o => o.cosmeticId == covered.Id);
            Assert.That(viaSubscription.viaSubscription, Is.True);
            Assert.That(viaSubscription.expiresAt, Is.Null, "a subscription claim lasts as long as the subscription");

            // Held both ways, it is listed once, as the grant: that is the claim that outlives the subscription.
            var viaGrant = mine.Where(o => o.cosmeticId == granted.Id).ToList();
            Assert.That(viaGrant, Has.Count.EqualTo(1));
            Assert.That(viaGrant[0].viaSubscription, Is.False);
            Assert.That(viaGrant[0].expiresAt, Is.EqualTo(grantEnds).Within(TimeSpan.FromMilliseconds(1)));

            Assert.That(theirs.Select(o => o.cosmeticId), Does.Not.Contain(covered.Id));
        });

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(((FailedEquip)await outsider.Cosmetics.Equip(new WornItem(covered.Id), ct)).error, Is.EqualTo(CosmeticError.NOT_OWNED));
            Assert.That(await subscriber.Cosmetics.Equip(new WornItem(covered.Id), ct), Is.InstanceOf<SuccessEquip>());
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task When_the_subscription_ends_what_it_covered_comes_off_and_the_rest_stays(CancellationToken ct = default)
    {
        var (wearer, onlooker, spaceId) = await TwoInASpaceAsync(ct);

        var frame = await PublishAsync("profile.frame", "ultima-frame", FramePayload, CosmeticAcquisitionMode.UltimaTier, ct: ct);
        var halo  = await PublishAsync("avatar.decoration", "free-halo", HaloPayload, ct: ct);
        var face  = await PublishAsync("option.font", "ultima-face", FontPayload, CosmeticAcquisitionMode.UltimaTier, ct: ct);

        await SubscribeAsync(wearer.UserId, ct);

        Assert.That(await wearer.Cosmetics.Equip(new WornItem(frame.Id), ct), Is.InstanceOf<SuccessEquip>());
        Assert.That(await wearer.Cosmetics.Equip(new WornItem(halo.Id), ct), Is.InstanceOf<SuccessEquip>());
        Assert.That(await wearer.Cosmetics.Equip(Look(font: face.Id, weight: 700), ct), Is.InstanceOf<SuccessEquip>());

        Assert.That(await SeenBy(onlooker, spaceId, wearer.UserId, ct), Has.Count.EqualTo(3));

        await EndSubscriptionAsync(wearer.UserId, ct);

        var seen = await Poll.ForValueAsync(() => SeenBy(onlooker, spaceId, wearer.UserId, ct),
            worn => worn.Count is 2, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200), ct);

        var nickname = seen.OfType<WornNickname>().SingleOrDefault();

        Assert.Multiple(() =>
        {
            Assert.That(Items(seen), Is.EqualTo(new[] { halo.Id }), "the subscription frame stayed on, or the free halo came off");
            Assert.That(nickname, Is.Not.Null, "the name lost what the wearer chose for themselves along with the face");
            Assert.That(nickname!.font, Is.Null, "the subscription face stayed on the name");
            Assert.That(nickname.weight, Is.EqualTo((ushort)700));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_name_styled_only_by_the_subscription_goes_plain_and_free_things_stay_on(CancellationToken ct = default)
    {
        var styled = await CreateSessionAsync(ct);
        var plain  = await CreateSessionAsync(ct);

        var face = await PublishAsync("option.font", "ultima-face", FontPayload, CosmeticAcquisitionMode.UltimaTier, ct: ct);
        var halo = await PublishAsync("avatar.decoration", "free-halo", HaloPayload, ct: ct);

        await SubscribeAsync(styled.UserId, ct);
        await SubscribeAsync(plain.UserId, ct);

        Assert.That(await styled.Cosmetics.Equip(Look(font: face.Id), ct), Is.InstanceOf<SuccessEquip>());
        Assert.That(await plain.Cosmetics.Equip(new WornItem(halo.Id), ct), Is.InstanceOf<SuccessEquip>());

        await EndSubscriptionAsync(styled.UserId, ct);
        await EndSubscriptionAsync(plain.UserId, ct);

        var styledNow = await WornByAsync(styled, worn => worn.Count is 0, ct);
        var plainNow  = await WornByAsync(plain, worn => worn.Count is 1, ct);

        Assert.Multiple(() =>
        {
            Assert.That(styledNow, Is.Empty, "a name with nothing left chosen is a name not styled");
            Assert.That(Items(plainNow), Is.EqualTo(new[] { halo.Id }));
        });
    }

    // ── Kinds and slots ─────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Putting_on_another_item_of_the_same_kind_replaces_the_first(CancellationToken ct = default)
    {
        var (wearer, onlooker, spaceId) = await TwoInASpaceAsync(ct);

        var first  = await PublishAsync("avatar.decoration", "first", HaloPayload, ct: ct);
        var second = await PublishAsync("avatar.decoration", "second", HaloPayload, ct: ct);

        Assert.That(await wearer.Cosmetics.Equip(new WornItem(first.Id), ct), Is.InstanceOf<SuccessEquip>());
        Assert.That(await wearer.Cosmetics.Equip(new WornItem(second.Id), ct), Is.InstanceOf<SuccessEquip>());

        Assert.That(await SeenBy(onlooker, spaceId, wearer.UserId, ct), Is.EqualTo(new IWornCosmetic[] { new WornItem(second.Id) }));

        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        Assert.That(await db.CosmeticEquips.CountAsync(e => e.UserId == wearer.UserId && e.KindKey == "avatar.decoration", ct), Is.EqualTo(1));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_row_of_a_kind_this_build_does_not_declare_is_never_offered_or_worn(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var orphan  = await PublishAsync("retired.kind", "orphan", "{}", ct: ct);

        var equipped  = await session.Cosmetics.Equip(new WornItem(orphan.Id), ct);
        var catalogue = await session.Cosmetics.GetCatalogue(ct);

        Assert.Multiple(() =>
        {
            Assert.That((equipped as FailedEquip)?.error, Is.EqualTo(CosmeticError.UNKNOWN_KIND));
            Assert.That(catalogue.items.Select(i => i.cosmeticId), Does.Not.Contain(orphan.Id));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task The_catalogue_leaves_out_a_row_that_does_not_fit_its_kind_and_lists_the_files_a_row_draws_with(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        // Pulled in further than the kind allows: the document fails the kind's own schema.
        var misfit = await PublishAsync("avatar.decoration", "misfit", """{"insetPct":99,"beneath":false}""", ct: ct);
        var framed = await PublishAsync("profile.frame", "framed", FramePayload, assets: new Dictionary<string, string>
        {
            ["primary"]   = "file-band",
            ["secondary"] = "file-ornament",
            ["backdrop"]  = "file-from-a-slot-this-build-lost"
        }, ct: ct);

        var catalogue = await session.Cosmetics.GetCatalogue(ct);
        var entry     = catalogue.items.Single(i => i.cosmeticId == framed.Id);

        Assert.Multiple(() =>
        {
            Assert.That(catalogue.items.Select(i => i.cosmeticId), Does.Not.Contain(misfit.Id));
            Assert.That(entry.assets.Select(a => (a.slot, a.fileId)),
                Is.EquivalentTo(new[] { (AssetSlot.Primary, "file-band"), (AssetSlot.Secondary, "file-ornament") }));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_switched_off_kind_cannot_be_put_on_but_can_still_be_taken_off(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var worn    = await PublishAsync("avatar.decoration", "worn", HaloPayload, ct: ct);
        var another = await PublishAsync("avatar.decoration", "another", HaloPayload, ct: ct);

        Assert.That(await session.Cosmetics.Equip(new WornItem(worn.Id), ct), Is.InstanceOf<SuccessEquip>());

        var flags = GetGrainFactory().GetGrain<IFeatureFlagGrain>(Guid.Empty);
        var cache = FactoryAsp.Services.GetRequiredService<HybridCache>();

        string[] switches = ["af.cosmetics.avatar-decoration.active", "af.cosmetics.nickname-style.active"];

        try
        {
            foreach (var flagId in switches)
            {
                var off     = new FeatureFlagInput(flagId, "switched off by CosmeticEquipTests", false, null, null, null, null);
                var created = await flags.CreateFlagAsync(off);

                if (!created.Success)
                    Assert.That((await flags.UpdateFlagAsync(off)).Success, Is.True, created.Error);
            }

            await cache.RemoveAsync(ICosmeticsCache.CatalogueKey, ct);

            var catalogue = await session.Cosmetics.GetCatalogue(ct);

            await Assert.MultipleAsync(async () =>
            {
                Assert.That(catalogue.enabledKinds, Does.Not.Contain("avatar.decoration").And.Not.Contain("nickname.style"));
                Assert.That(catalogue.enabledKinds, Does.Contain("profile.frame"));

                Assert.That(((FailedEquip)await session.Cosmetics.Equip(new WornItem(another.Id), ct)).error, Is.EqualTo(CosmeticError.KIND_DISABLED));
                Assert.That(((FailedEquip)await session.Cosmetics.Equip(Look(weight: 700), ct)).error, Is.EqualTo(CosmeticError.KIND_DISABLED));

                // A kind switched off is exactly when somebody may want to take theirs off.
                Assert.That(await session.Cosmetics.Unequip("avatar.decoration", ct), Is.InstanceOf<SuccessEquip>());
            });
        }
        finally
        {
            foreach (var flagId in switches)
                await flags.DeleteFlagAsync(flagId);

            await cache.RemoveAsync(ICosmeticsCache.CatalogueKey, ct);
        }

        Assert.That(await session.Cosmetics.Equip(new WornItem(another.Id), ct), Is.InstanceOf<SuccessEquip>(), "the kind stayed off after its flag was deleted");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_grant_extended_while_worn_keeps_the_item_on_until_its_new_end(CancellationToken ct = default)
    {
        var (wearer, onlooker, spaceId) = await TwoInASpaceAsync(ct);
        var frame = await PublishAsync("profile.frame", "extended", FramePayload, CosmeticAcquisitionMode.OperatorGrant, ct: ct);

        var firstEnd = DateTimeOffset.UtcNow.AddSeconds(4);
        await GrantAsync(wearer.UserId, frame.Id, ct, expiresAt: firstEnd);

        Assert.That(await wearer.Cosmetics.Equip(new WornItem(frame.Id), ct), Is.InstanceOf<SuccessEquip>());

        // Extended behind the grain's back: its lapse reminder is still set for the first end.
        var newEnd = DateTimeOffset.UtcNow.AddSeconds(14);

        await using (var db = await FactoryAsp.Services
                        .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                        .CreateDbContextAsync(ct))
        {
            await db.CosmeticOwnerships
               .Where(g => g.UserId == wearer.UserId && g.CosmeticItemId == frame.Id)
               .ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, newEnd), ct);
        }

        await Task.Delay(firstEnd - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3), ct);

        Assert.That(Items(await SeenBy(onlooker, spaceId, wearer.UserId, ct)), Is.EqualTo(new[] { frame.Id }),
            "the frame came off at the grant's old end");

        // Only a reminder set again for the new end takes it off now; the old one would not retry for an hour.
        var seen = await Poll.ForValueAsync(() => SeenBy(onlooker, spaceId, wearer.UserId, ct),
            worn => worn.Count is 0, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), ct);

        Assert.Multiple(() =>
        {
            Assert.That(Items(seen), Is.Empty, "the frame stayed on after the extended grant ran out");
            Assert.That(DateTimeOffset.UtcNow, Is.GreaterThanOrEqualTo(newEnd));
        });
    }
}
