namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Features.Admin;
using ArgonContracts;
using ConsoleContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Wearing things, and what the clients that predate wearing things still see.
/// </summary>
/// <remarks>
/// <para>One fixture rather than the two the plan named, because both halves need the same expensive
/// setup — an operator creating and publishing a cosmetic, then granting it — and a second fixture
/// would boot the whole environment again to repeat it.</para>
///
/// <para>The half that matters most is the last section. Every pre-cosmetics field on the profile is
/// now produced by <c>CosmeticProfileProjection</c> rather than read straight off the row, and an
/// installed desktop build reads exactly those fields. A regression there is invisible from any
/// current client and breaks every older one.</para>
/// </remarks>
[TestFixture]
public class CosmeticEquipTests : TestBase
{
    private static readonly Guid OperatorId = Guid.Parse("00000000-0000-0000-0000-0000000ad003");

    [OneTimeSetUp]
    public async Task SeedOperator()
    {
        await using var db = await NewDbAsync(CancellationToken.None);

        if (await db.Operators.AnyAsync(o => o.Id == OperatorId, CancellationToken.None))
            return;

        db.Operators.Add(new OperatorEntity
        {
            Id               = OperatorId,
            DisplayName      = "Cosmetics Equip Test Operator",
            Email            = "equip-operator@argon.test",
            IsActive         = true,
            IsSystemOperator = true
        });

        await db.SaveChangesAsync(CancellationToken.None);
    }

    private (AsyncServiceScope Scope, IAdminConsole Console) Admin()
    {
        var scope = FactoryAsp.Services.CreateAsyncScope();

        OperatorRequestContext.Set(new OperatorRequestContextData
        {
            UserId                = Guid.Parse("00000000-0000-0000-0000-0000000ad000"),
            OperatorId            = OperatorId,
            Email                 = "equip-operator@argon.test",
            CertificateThumbprint = "TEST-THUMBPRINT"
        });

        return (scope, scope.ServiceProvider.GetRequiredService<IAdminConsole>());
    }

    private async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    private static string UniqueSlug() => $"e{Guid.NewGuid():N}"[..12];

    /// <summary>
    /// A published cosmetic that needs no asset file, granted to one account.
    /// </summary>
    /// <remarks>
    /// A badge, declared as carrying a picture of its wearer's rather than one from the catalogue —
    /// which is how a row publishes with no file behind it. It used to be a nickname style, until
    /// that kind turned out to have nothing of its own to carry and stopped having rows at all.
    /// </remarks>
    private async Task<Guid> PublishedStyleGrantedTo(Guid userId, CancellationToken ct)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var created = await admin.CreateCosmetic(
            new CreateCosmeticInput("profile.badge", UniqueSlug(), "cosmetic_test_badge", null, "rare", 0,
                """{"tooltipKey":"cosmetic_test_badge"}""", CosmeticAssetSourceKind.UserProvided,
                "Test Badge", null), ct);

        Assert.That(created.success, Is.True, created.error);

        var id = created.cosmeticId!.Value;

        var published = await admin.PublishCosmetic(id, ct);
        Assert.That(published.success, Is.True, published.detail);

        var granted = await admin.GrantCosmetic(userId, id, null, ct);
        Assert.That(granted.success, Is.True, granted.error);

        return id;
    }

    private async Task<(Guid UserId, ICosmeticsInteraction Cosmetics, IUserInteraction Users)> SignedInAsync(CancellationToken ct)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var users  = GetUserService(scope.ServiceProvider);
        var userId = (await users.GetMe(ct)).userId;

        return (userId, IonClient.ForService<ICosmeticsInteraction>(FactoryAsp.Services), users);
    }

    // ── Loadouts ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task A_new_account_has_no_loadouts(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        var loadouts = await cosmetics.GetMyLoadouts(ct);

        Assert.That(loadouts.loadouts, Is.Empty);
    }

    [Test, CancelAfter(180_000)]
    public async Task The_first_loadout_becomes_the_default(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        var created = await cosmetics.CreateLoadout("Everyday", ct);
        Assert.That(created, Is.InstanceOf<SuccessCreateLoadout>());

        var second = await cosmetics.CreateLoadout("Weekends", ct);
        Assert.That(second, Is.InstanceOf<SuccessCreateLoadout>());

        var loadouts = await cosmetics.GetMyLoadouts(ct);

        Assert.Multiple(() =>
        {
            Assert.That(loadouts.loadouts.Count(), Is.EqualTo(2));
            Assert.That(loadouts.loadouts.Count(x => x.isDefault), Is.EqualTo(1));
            Assert.That(loadouts.loadouts.First(x => x.isDefault).name, Is.EqualTo("Everyday"));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Two_loadouts_cannot_share_a_name(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        await cosmetics.CreateLoadout("Everyday", ct);
        var again = await cosmetics.CreateLoadout("everyday", ct);

        Assert.That(again, Is.InstanceOf<FailedCreateLoadout>());
        Assert.That(((FailedCreateLoadout)again).error, Is.EqualTo(CosmeticError.NAME_TAKEN));
    }

    /// <summary>
    /// Deleting the look worn everywhere would silently strip every space that has not chosen one,
    /// so it is refused while other looks exist.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task The_default_cannot_be_deleted_while_other_looks_exist(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        var first = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);
        await cosmetics.CreateLoadout("Weekends", ct);

        var deleted = await cosmetics.DeleteLoadout(first.loadoutId, ct);

        Assert.That(deleted, Is.InstanceOf<FailedLoadoutAction>());
        Assert.That(((FailedLoadoutAction)deleted).error, Is.EqualTo(CosmeticError.CANNOT_DELETE_DEFAULT));
    }

    /// <summary>
    /// The last one can, and that distinction is the whole of it.
    /// </summary>
    /// <remarks>
    /// The first look a person creates is also the default, so a flat "the default cannot be deleted"
    /// locked them into whatever they made first with no way back — which is exactly what somebody
    /// hit the first time they tried it. Having no looks is the state every account starts in.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task The_only_look_can_be_deleted(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        var only    = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);
        var deleted = await cosmetics.DeleteLoadout(only.loadoutId, ct);

        Assert.That(deleted, Is.InstanceOf<SuccessLoadoutAction>(),
            $"refused: {(deleted as FailedLoadoutAction)?.error}");

        Assert.That((await cosmetics.GetMyLoadouts(ct)).loadouts, Is.Empty);
    }

    /// <summary>
    /// Moving the default is what stops the first loadout somebody ever made being the one they are
    /// stuck with — it cannot be deleted while it holds the default.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task The_default_can_be_moved_and_the_old_one_then_deleted(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        var first  = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);
        var second = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Weekends", ct);

        var moved = await cosmetics.SetDefaultLoadout(second.loadoutId, ct);
        Assert.That(moved, Is.InstanceOf<SuccessLoadoutAction>());

        var loadouts = await cosmetics.GetMyLoadouts(ct);

        Assert.Multiple(() =>
        {
            Assert.That(loadouts.loadouts.Count(x => x.isDefault), Is.EqualTo(1), "there is always exactly one");
            Assert.That(loadouts.loadouts.Single(x => x.isDefault).loadoutId, Is.EqualTo(second.loadoutId));
        });

        var deleted = await cosmetics.DeleteLoadout(first.loadoutId, ct);
        Assert.That(deleted, Is.InstanceOf<SuccessLoadoutAction>());
    }

    [Test, CancelAfter(180_000)]
    public async Task Making_the_default_the_default_again_changes_nothing(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        var only = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);

        Assert.That(await cosmetics.SetDefaultLoadout(only.loadoutId, ct), Is.InstanceOf<SuccessLoadoutAction>());
        Assert.That((await cosmetics.GetMyLoadouts(ct)).loadouts.Count(x => x.isDefault), Is.EqualTo(1));
    }

    /// <summary>
    /// A look is worn everywhere for exactly as long as it holds the global assignment.
    /// </summary>
    /// <remarks>
    /// <para>The flag used to be a tier of its own underneath the assignments, and it was a second
    /// way of saying "everywhere" that the person could not see: the first look anybody creates is
    /// flagged, so choosing one space for it left them still wearing it in every other space, with
    /// the picker saying the opposite.</para>
    ///
    /// <para>So the thing to pin is that the flag never outlives the row. Release the global scope
    /// and nobody is wearing a look everywhere — which used to be impossible to reach, and is what
    /// "worn only in the spaces I picked" is built on.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_look_is_worn_everywhere_only_while_it_holds_the_global_scope(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        var only = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);

        var created = await cosmetics.GetMyLoadouts(ct);

        Assert.Multiple(() =>
        {
            Assert.That(GlobalScopeOf(created), Is.EqualTo(only.loadoutId), "the first look is worn everywhere");
            Assert.That(created.loadouts.Single().isDefault, Is.True, "and the flag says so");
        });

        Assert.That(await cosmetics.UnassignLoadoutFromSpace(null, ct), Is.InstanceOf<SuccessLoadoutAction>());

        var released = await cosmetics.GetMyLoadouts(ct);

        Assert.Multiple(() =>
        {
            Assert.That(GlobalScopeOf(released), Is.Null, "nothing is worn everywhere any more");
            Assert.That(released.loadouts.Count(x => x.isDefault), Is.Zero, "and nothing still claims to be");
        });
    }

    /// <summary>
    /// The part of something worn that only its wearer decides, and how narrow the gate on it is.
    /// </summary>
    /// <remarks>
    /// <para>This is the one thing in the system that is neither authored by an operator nor picked
    /// from a list: a person's own colours on their own name. It goes into the same column as a board
    /// card's content and through the same validator, which is what this pins — the strictness is the
    /// feature, because the value ends up in a style attribute on everybody who looks at that name.
    /// </para>
    ///
    /// <para>Both refusals matter and they fail for different reasons: a string that is not a colour
    /// is caught by the schema's own rule, and a field the schema never declared is caught by the
    /// deserializer refusing unknown members.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_wearer_colours_their_own_name_and_anything_else_is_refused(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        var loadout = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);

        const string mine = """{"stops":["#22d3ee","#a855f7"],"angle":45}""";

        // No equip first: a nickname style is bare, so configuring it is what puts it on.
        var tuned = await cosmetics.ConfigureCosmetic(loadout.loadoutId, "nickname.style", 0, null, mine, ct);
        Assert.That(tuned, Is.InstanceOf<SuccessEquip>(), $"tuning refused: {(tuned as FailedEquip)?.error}");

        var worn = (await cosmetics.GetMyLoadouts(ct)).loadouts
           .Single(x => x.loadoutId == loadout.loadoutId).equipped
           .Single(x => x.kindKey == "nickname.style");

        Assert.Multiple(() =>
        {
            Assert.That(worn.contentJson, Is.EqualTo(mine), "it reads back as it was written");
            Assert.That(worn.itemId, Is.EqualTo(Guid.Empty), "and it wears no catalogue row");
        });

        // Sequential rather than in an Assert.Multiple block: that block takes a void delegate, so an
        // async one runs on after it and its failures land outside the test.
        Assert.That(
            await cosmetics.ConfigureCosmetic(loadout.loadoutId, "nickname.style", 0, null, """{"stops":["red"]}""", ct),
            Is.InstanceOf<FailedEquip>(), "'red' is not a colour this writes into a style attribute");

        Assert.That(
            await cosmetics.ConfigureCosmetic(loadout.loadoutId, "nickname.style", 0, null, """{"shadow":true}""", ct),
            Is.InstanceOf<FailedEquip>(), "the schema declares no such field");

        Assert.That(
            await cosmetics.ConfigureCosmetic(loadout.loadoutId, "profile.background", 0, null, mine, ct),
            Is.InstanceOf<FailedEquip>(), "a kind with nothing for its wearer to decide takes no tuning");
    }

    /// <summary>
    /// Moving "everywhere" from one look to another, which is one button in the picker.
    /// </summary>
    /// <remarks>
    /// <para>This is the shape that broke in somebody's hands. Exactly one look per person may carry
    /// the flag, and the database holds that with a unique index it checks as each row is written —
    /// so clearing the old flag and setting the new one in the same write fails whenever the two
    /// land in that order, which is an order nothing in the code chooses.</para>
    ///
    /// <para>The existing move test did not catch it because it moved the flag off the first look a
    /// person made, and the writes happened to come out in the safe order. This one moves it back
    /// and forth, so neither order is the lucky one.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task Everywhere_moves_between_looks_in_both_directions(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        var first  = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);
        var second = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Weekends", ct);

        foreach (var loadoutId in new[] { second.loadoutId, first.loadoutId, second.loadoutId })
        {
            var assigned = await cosmetics.AssignLoadoutToSpace(null, loadoutId, ct);

            Assert.That(assigned, Is.InstanceOf<SuccessLoadoutAction>(),
                $"refused: {(assigned as FailedLoadoutAction)?.error}");

            var loadouts = await cosmetics.GetMyLoadouts(ct);

            Assert.Multiple(() =>
            {
                Assert.That(GlobalScopeOf(loadouts), Is.EqualTo(loadoutId), "the scope moved");
                Assert.That(loadouts.loadouts.Count(x => x.isDefault), Is.EqualTo(1), "and exactly one is flagged");
                Assert.That(loadouts.loadouts.Single(x => x.isDefault).loadoutId, Is.EqualTo(loadoutId),
                    "and it is the one the scope points at");
            });
        }
    }

    /// <summary>Which look the global scope points at, if any.</summary>
    private static Guid? GlobalScopeOf(CosmeticLoadoutList list)
    {
        foreach (var assignment in list.assignments)
        {
            if (assignment.spaceId is null)
                return assignment.loadoutId;
        }

        return null;
    }

    // ── Equipping ───────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task Equipping_shows_up_on_the_profile(CancellationToken ct = default)
    {
        var (userId, cosmetics, users) = await SignedInAsync(ct);

        var cosmeticId = await PublishedStyleGrantedTo(userId, ct);
        var loadout    = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);

        var equipped = await cosmetics.Equip(loadout.loadoutId, cosmeticId, 0, null, ct);

        Assert.That(equipped, Is.InstanceOf<SuccessEquip>(),
            $"equip refused: {(equipped as FailedEquip)?.error}");

        // The echo, then a fresh read: the echo proves the write built the DTO, the read proves it
        // reached the database rather than only the response.
        var echoed = ((SuccessEquip)equipped).profile;
        var fetched = await users.GetMyProfile(ct);

        Assert.Multiple(() =>
        {
            Assert.That(echoed.cosmetics, Is.Not.Null);
            Assert.That(echoed.cosmetics!.Value.Values.Single().itemId, Is.EqualTo(cosmeticId));
            Assert.That(echoed.loadoutId, Is.EqualTo(loadout.loadoutId));

            Assert.That(fetched.cosmetics, Is.Not.Null);
            Assert.That(fetched.cosmetics!.Value.Values.Single().kindKey, Is.EqualTo("profile.badge"));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Equipping_something_you_do_not_own_is_refused(CancellationToken ct = default)
    {
        var (_, cosmetics, _) = await SignedInAsync(ct);

        // Published, but granted to nobody.
        var (scope, admin) = Admin();
        await using var _ = scope;

        var created = await admin.CreateCosmetic(
            new CreateCosmeticInput("profile.badge", UniqueSlug(), "cosmetic_test_badge", null, null, 0,
                """{"tooltipKey":"cosmetic_test_badge"}""", CosmeticAssetSourceKind.UserProvided,
                "Test Badge", null), ct);

        var id = created.cosmeticId!.Value;
        await admin.PublishCosmetic(id, ct);

        var loadout = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);
        var equipped = await cosmetics.Equip(loadout.loadoutId, id, 0, null, ct);

        Assert.That(equipped, Is.InstanceOf<FailedEquip>());
        Assert.That(((FailedEquip)equipped).error, Is.EqualTo(CosmeticError.NOT_OWNED));
    }

    [Test, CancelAfter(180_000)]
    public async Task An_unpublished_cosmetic_cannot_be_worn(CancellationToken ct = default)
    {
        var (userId, cosmetics, _) = await SignedInAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var created = await admin.CreateCosmetic(
            new CreateCosmeticInput("profile.badge", UniqueSlug(), "cosmetic_test_badge", null, null, 0,
                """{"tooltipKey":"cosmetic_test_badge"}""", CosmeticAssetSourceKind.UserProvided,
                "Test Badge", null), ct);

        var id = created.cosmeticId!.Value;
        await admin.GrantCosmetic(userId, id, null, ct);

        var loadout  = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);
        var equipped = await cosmetics.Equip(loadout.loadoutId, id, 0, null, ct);

        Assert.That(equipped, Is.InstanceOf<FailedEquip>());
        Assert.That(((FailedEquip)equipped).error, Is.EqualTo(CosmeticError.ITEM_UNAVAILABLE));
    }

    [Test, CancelAfter(180_000)]
    public async Task A_slot_the_kind_does_not_have_is_refused(CancellationToken ct = default)
    {
        var (userId, cosmetics, _) = await SignedInAsync(ct);

        var cosmeticId = await PublishedStyleGrantedTo(userId, ct);
        var loadout    = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);

        // profile.badge has eight slots, so this is past the end of them.
        var equipped = await cosmetics.Equip(loadout.loadoutId, cosmeticId, 99, null, ct);

        Assert.That(equipped, Is.InstanceOf<FailedEquip>());
        Assert.That(((FailedEquip)equipped).error, Is.EqualTo(CosmeticError.SLOT_OUT_OF_RANGE));
    }

    /// <summary>
    /// Taking off something that is not on is the end state that was asked for, not an error.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Unequipping_is_idempotent(CancellationToken ct = default)
    {
        var (userId, cosmetics, _) = await SignedInAsync(ct);

        var cosmeticId = await PublishedStyleGrantedTo(userId, ct);
        var loadout    = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);

        await cosmetics.Equip(loadout.loadoutId, cosmeticId, 0, null, ct);

        var first  = await cosmetics.Unequip(loadout.loadoutId, "profile.badge", 0, ct);
        var second = await cosmetics.Unequip(loadout.loadoutId, "profile.badge", 0, ct);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.InstanceOf<SuccessEquip>());
            Assert.That(second, Is.InstanceOf<SuccessEquip>());
            Assert.That(((SuccessEquip)second).profile.cosmetics, Is.Empty);
        });
    }

    /// <summary>
    /// Revoking a grant stops the thing rendering without touching what the person put on — so
    /// restoring the grant puts them back in what they were wearing.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Revoking_a_grant_stops_it_rendering_but_keeps_it_equipped(CancellationToken ct = default)
    {
        var (userId, cosmetics, users) = await SignedInAsync(ct);

        var cosmeticId = await PublishedStyleGrantedTo(userId, ct);
        var loadout    = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);

        await cosmetics.Equip(loadout.loadoutId, cosmeticId, 0, null, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        await admin.RevokeCosmetic(userId, cosmeticId, ct);
        Assert.That((await users.GetMyProfile(ct)).cosmetics, Is.Empty);

        await using var db = await NewDbAsync(ct);
        Assert.That(await db.CosmeticEquips.AnyAsync(x => x.CosmeticItemId == cosmeticId, ct), Is.True,
            "the equipped row should survive a revocation");

        await admin.GrantCosmetic(userId, cosmeticId, null, ct);
        Assert.That((await users.GetMyProfile(ct)).cosmetics!.Value.Values.Single().itemId, Is.EqualTo(cosmeticId));
    }

    /// <summary>
    /// The switch the whole engine exists for, seen from the wearer's profile.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Disabling_a_kind_hides_it_without_unequipping_anything(CancellationToken ct = default)
    {
        var (userId, cosmetics, users) = await SignedInAsync(ct);

        var cosmeticId = await PublishedStyleGrantedTo(userId, ct);
        var loadout    = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);

        await cosmetics.Equip(loadout.loadoutId, cosmeticId, 0, null, ct);
        Assert.That((await users.GetMyProfile(ct)).cosmetics, Is.Not.Empty);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var off = await admin.SetCosmeticKindEnabled("profile.badge", false, ct);
        Assert.That(off.success, Is.True, off.error);

        Assert.That((await users.GetMyProfile(ct)).cosmetics, Is.Empty, "a disabled kind must not be served");

        await using (var db = await NewDbAsync(ct))
        {
            Assert.That(await db.CosmeticEquips.AnyAsync(x => x.CosmeticItemId == cosmeticId, ct), Is.True,
                "disabling a kind must not unequip anybody");
        }

        var on = await admin.SetCosmeticKindEnabled("profile.badge", true, ct);
        Assert.That(on.success, Is.True, on.error);

        Assert.That((await users.GetMyProfile(ct)).cosmetics!.Value.Values.Single().itemId, Is.EqualTo(cosmeticId),
            "switching the kind back on must restore what people were wearing");
    }

    // ── The batch the lists read from ───────────────────────────────────────────────────────────

    /// <summary>
    /// Without a space there is nothing proving the caller has met anybody, so a bulk read answers
    /// only about themselves.
    /// </summary>
    /// <remarks>
    /// This is the one method here that takes a list of strangers' ids, which makes it the one that
    /// could walk the directory. The per-person lookup answers that question properly through
    /// SocialReach; this one refuses to be asked it.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task GetWornBy_WithoutASpace_AnswersOnlyAboutTheCaller(CancellationToken ct = default)
    {
        var (userId, cosmetics, _) = await SignedInAsync(ct);

        var cosmeticId = await PublishedStyleGrantedTo(userId, ct);
        var loadout    = (SuccessCreateLoadout)await cosmetics.CreateLoadout("Everyday", ct);

        await cosmetics.Equip(loadout.loadoutId, cosmeticId, 0, null, ct);

        var stranger = Guid.Parse("00000000-0000-0000-0000-00000000dead");
        var worn     = await cosmetics.GetWornBy(null, new IonArray<Guid>([userId, stranger]), ct);

        Assert.That(worn.Select(x => x.userId), Is.EquivalentTo(new[] { userId }));
    }

    /// <summary>
    /// A space id is not a key: asking about a space you are not in answers nothing.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task GetWornBy_InASpaceTheCallerIsNotIn_AnswersNothing(CancellationToken ct = default)
    {
        var (userId, cosmetics, _) = await SignedInAsync(ct);

        var elsewhere = Guid.Parse("00000000-0000-0000-0000-00000000f00d");
        var worn      = await cosmetics.GetWornBy(elsewhere, new IonArray<Guid>([userId]), ct);

        Assert.That(worn, Is.Empty);
    }

    [Test, CancelAfter(180_000)]
    public async Task GetWornBy_SaysNothingAboutSomebodyWearingNothing(CancellationToken ct = default)
    {
        var (userId, cosmetics, _) = await SignedInAsync(ct);

        var worn = await cosmetics.GetWornBy(null, new IonArray<Guid>([userId]), ct);

        Assert.That(worn, Is.Empty, "a person wearing nothing takes no room in the answer");
    }

    // ── What an older client still sees ─────────────────────────────────────────────────────────

    /// <summary>
    /// The five backgrounds that shipped as hardcoded ids still answer to those ids.
    /// </summary>
    /// <remarks>
    /// <c>ProfilePresetValidator</c> is gone and the catalogue answers instead, so this is the test
    /// that the seed migration ran and that an installed build setting a background still works.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_legacy_background_id_is_still_accepted(CancellationToken ct = default)
    {
        var (userId, _, users) = await SignedInAsync(ct);

        await GrantUltimaAsync(userId, ct);

        var result = await users.UpdateMe(BackgroundOnly(3), ct);

        Assert.That(result, Is.InstanceOf<SuccessUpdateMe>(),
            $"legacy background refused: {(result as FailedUpdateMe)?.error}");
        Assert.That(((SuccessUpdateMe)result).profile.backgroundId, Is.EqualTo(3));

        var fetched = await users.GetMyProfile(ct);
        Assert.That(fetched.backgroundId, Is.EqualTo(3), "the stored value must survive the projection");
    }

    [Test, CancelAfter(180_000)]
    public async Task A_background_id_no_catalogue_row_answers_to_is_refused(CancellationToken ct = default)
    {
        var (userId, _, users) = await SignedInAsync(ct);

        await GrantUltimaAsync(userId, ct);

        var result = await users.UpdateMe(BackgroundOnly(99), ct);

        Assert.That(result, Is.InstanceOf<FailedUpdateMe>());
        Assert.That(((FailedUpdateMe)result).error, Is.EqualTo(UpdateMeError.INVALID_PRESET_ID));
    }

    /// <summary>
    /// The new fields are present and empty for somebody who has never worn anything, rather than
    /// absent — an older client skips them, and a new one must not have to guess.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task A_profile_with_no_cosmetics_still_carries_the_new_fields(CancellationToken ct = default)
    {
        var (_, _, users) = await SignedInAsync(ct);

        var profile = await users.GetMyProfile(ct);

        Assert.Multiple(() =>
        {
            Assert.That(profile.loadoutId, Is.Null);
            Assert.That(profile.cosmetics, Is.Null.Or.Empty);
        });
    }

    /// <summary>
    /// The catalogue carries an operator's own names, in every language they wrote one, not just
    /// the fallback key a client falls back to when it has nothing else.
    /// </summary>
    /// <remarks>
    /// <c>GetCatalogueAsync</c> resolves <c>CatalogueCosmetic.text</c> from
    /// <c>CosmeticTranslations</c> and nothing else on the server ever reads it back — the name is
    /// rendered client-side, so a <c>text</c> array going out empty, or carrying only the fallback
    /// locale, would fail no assertion anywhere else in this suite. That is exactly the shape of the
    /// gap that took the dev stand down: the table this method joins against did not exist yet, and
    /// nothing server-side noticed. This test is the one place that reads <c>text</c> back and checks
    /// a non-English locale by name, since serving only the fallback is the likeliest way for this to
    /// be quietly wrong again.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task The_catalogue_carries_every_translation_of_a_cosmetics_name(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var created = await admin.CreateCosmetic(
            new CreateCosmeticInput("profile.badge", UniqueSlug(), "cosmetic_test_badge", null, "rare", 0,
                """{"tooltipKey":"cosmetic_test_badge"}""", CosmeticAssetSourceKind.UserProvided,
                "Test Badge", null), ct);

        Assert.That(created.success, Is.True, created.error);
        var id = created.cosmeticId!.Value;

        var published = await admin.PublishCosmetic(id, ct);
        Assert.That(published.success, Is.True, published.detail);

        const string armenianName = "Փորձնական կրծքանշան";

        var translated = await admin.SetCosmeticTranslation(new CosmeticTranslationInput(id, "am", armenianName, null), ct);
        Assert.That(translated.success, Is.True, translated.error);

        var (_, cosmetics, _) = await SignedInAsync(ct);

        var catalogue = await cosmetics.GetCatalogue(ct);
        var item = catalogue.items.Single(x => x.cosmeticId == id);

        var textByLocale = item.text!.Value.ToDictionary(x => x.locale, x => x.name);

        Assert.Multiple(() =>
        {
            Assert.That(item.nameKey, Is.EqualTo("cosmetic_test_badge"), "still sent as the client's last resort");
            Assert.That(textByLocale.Keys, Is.EquivalentTo(new[] { "en", "am" }));
            Assert.That(textByLocale["en"], Is.EqualTo("Test Badge"));
            Assert.That(textByLocale["am"], Is.EqualTo(armenianName), "the non-fallback locale, not just en repeated");
        });
    }

    private static UserEditInput BackgroundOnly(int backgroundId)
        => new(null, null, backgroundId, null, null, null, null, null, null, null, null);

    private async Task GrantUltimaAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        var user = await db.Users.FirstAsync(x => x.Id == userId, ct);
        user.HasActiveUltima = true;

        await db.SaveChangesAsync(ct);
    }
}
