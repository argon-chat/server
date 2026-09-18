namespace ArgonComplexTest.Tests;

using Argon.Api.Entities.Data;
using Argon.Entities;
using Argon.Features.Admin;
using Argon.Features.Clustering.Regions;
using ConsoleContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Spending a key: the item a case drops, exchanged for ownership of a cosmetic when it is used.
/// </summary>
/// <remarks>
/// <para>Driven through <c>IInventoryInteraction.UseItem</c> rather than the grain directly, because
/// the caller's identity is the whole of the authorisation here — the grain reads it off the ambient
/// request context — and going in over the signed-in client is what makes the test act as the person
/// who owns the key rather than as whoever set a context value last.</para>
///
/// <para>The catalogue rows are seeded straight into the database rather than authored through the
/// admin console, which would cost three calls per test and buy nothing. They are seeded
/// <i>published</i>, because that is the state a key is meant to be spent in: a key hands over
/// ownership of something the player is then able to wear, and a row that is not servable would be
/// refused rather than granted (see
/// <see cref="A_key_for_an_unpublished_cosmetic_is_refused_and_keeps_the_item"/>).</para>
///
/// <para><b>Two of these tests are the only thing that has ever exercised what they cover.</b>
/// The duplicate refusal and the revival of a lapsed grant are reachable from no other caller — the
/// operator console grants with <c>CosmeticGrantIntent.Ensure</c>, and it names itself as the source
/// with no item behind it, so neither rule has a second witness.</para>
/// </remarks>
[TestFixture]
public class CosmeticKeyItemTests : TestBase
{
    // Distinct from CosmeticAdminTests' OperatorId: the two fixtures land in the same shard, and two
    // OneTimeSetUp methods seeding the same operator row would race each other there.
    private static readonly Guid OperatorId = Guid.Parse("00000000-0000-0000-0000-0000000ad004");

    [OneTimeSetUp]
    public async Task SeedOperator()
    {
        await using var db = await NewDbAsync(CancellationToken.None);

        if (await db.Operators.AnyAsync(o => o.Id == OperatorId, CancellationToken.None))
            return;

        db.Operators.Add(new OperatorEntity
        {
            Id               = OperatorId,
            DisplayName      = "Cosmetic Key Test Operator",
            Email            = "cosmetic-key-operator@argon.test",
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
            Email                 = "cosmetic-key-operator@argon.test",
            CertificateThumbprint = "TEST-THUMBPRINT"
        });

        return (scope, scope.ServiceProvider.GetRequiredService<IAdminConsole>());
    }

    private async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    private async Task<Guid> RegisterUserAsync(CancellationToken ct)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);
        return (await GetUserService(scope.ServiceProvider).GetMe(ct)).userId;
    }

    private static string UniqueSlug() => $"k{Guid.NewGuid():N}"[..12];

    /// <summary>
    /// A badge whose picture is its wearer's, which is the kind of row that needs no uploaded file.
    /// Published unless a test is about what happens when it is not.
    /// </summary>
    private async Task<Guid> SeedCosmeticAsync(CancellationToken ct, bool published = true)
    {
        await using var db = await NewDbAsync(ct);

        var slug = UniqueSlug();

        var cosmetic = new CosmeticItemEntity
        {
            Id          = ArgonId.New(),
            KindKey     = "profile.badge",
            Slug        = slug,
            NameKey     = $"cosmetic_{slug}",
            Rarity      = "rare",
            Payload     = $$"""{"tooltipKey":"cosmetic_{{slug}}"}""",
            AssetSource = CosmeticAssetSource.UserProvided,
            IsPublished = published,
            PublishedAt = published ? DateTimeOffset.UtcNow : null
        };

        db.Cosmetics.Add(cosmetic);

        await db.SaveChangesAsync(ct);

        return cosmetic.Id;
    }

    /// <summary>An owned, usable key for one cosmetic. Null days is a permanent grant.</summary>
    private async Task<Guid> SeedKeyAsync(Guid userId, Guid cosmeticId, int? durationDays, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        var scenario = new CosmeticScenario
        {
            Key          = ArgonId.New(),
            CosmeticId   = cosmeticId,
            DurationDays = durationDays
        };

        var key = new ArgonItemEntity
        {
            Id            = ArgonId.New(),
            OwnerId       = userId,
            TemplateId    = "cosmetic_key_test",
            IsReference   = false,
            IsUsable      = true,
            IsGiftable    = true,
            IsAffectBadge = false,
            ReceivedFrom  = null,
            Scenario      = scenario,
            ScenarioKey   = scenario.Key,
            CreatedAt     = DateTimeOffset.UtcNow
        };

        db.Items.Add(key);

        await db.SaveChangesAsync(ct);

        return key.Id;
    }

    private async Task<bool> UseAsync(Guid itemId, CancellationToken ct)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        return await GetInventoryService(scope.ServiceProvider).UseItem(itemId, ct);
    }

    /// <summary>
    /// The whole exchange: the key is gone and the ownership it bought names it.
    /// </summary>
    /// <remarks>
    /// The expiry is held to five minutes of now plus thirty days. Loose enough that nothing here
    /// depends on how long the test took, tight enough to say which moment the window is counted
    /// from — and that is the rule worth holding: a key sat on for a month must still grant its full
    /// thirty days, so "not null" would pass just as happily on a date counted from when the item was
    /// received.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task Using_a_key_grants_its_cosmetic(CancellationToken ct = default)
    {
        var userId     = await RegisterUserAsync(ct);
        var cosmeticId = await SeedCosmeticAsync(ct);
        var keyId      = await SeedKeyAsync(userId, cosmeticId, 30, ct);

        Assert.That(await UseAsync(keyId, ct), Is.True, "the key should have been spent");

        await using var db = await NewDbAsync(ct);

        var ownership = await db.CosmeticOwnerships
           .FirstOrDefaultAsync(x => x.UserId == userId && x.CosmeticItemId == cosmeticId, ct);

        Assert.That(ownership, Is.Not.Null, "using a key is how the ownership row comes to exist");

        var keySurvives = await db.Items.AnyAsync(x => x.Id == keyId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ownership!.Source, Is.EqualTo(CosmeticOwnershipSource.Item));
            Assert.That(ownership.InventoryItemId, Is.EqualTo(keyId), "the row names the key it was spent for");
            Assert.That(ownership.ExpiresAt, Is.EqualTo(DateTimeOffset.UtcNow.AddDays(30)).Within(5).Minutes,
                "thirty days from the moment of use, not from when the key was received");
            Assert.That(keySurvives, Is.False, "a spent key is gone");
        });
    }

    /// <summary>
    /// A second key for something already owned is refused, and stays in the inventory.
    /// </summary>
    /// <remarks>
    /// This is the behaviour the whole duplicate policy exists for. A spare key is a thing that can be
    /// gifted or kept; consuming it for nothing would take property off somebody for pressing a button
    /// twice, and the only visible difference between that and a working grant is an item that is no
    /// longer there.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_duplicate_key_is_refused_and_keeps_the_item(CancellationToken ct = default)
    {
        var userId     = await RegisterUserAsync(ct);
        var cosmeticId = await SeedCosmeticAsync(ct);
        var firstKey   = await SeedKeyAsync(userId, cosmeticId, null, ct);
        var spareKey   = await SeedKeyAsync(userId, cosmeticId, null, ct);

        Assert.That(await UseAsync(firstKey, ct), Is.True, "the first key should have been spent");
        Assert.That(await UseAsync(spareKey, ct), Is.False, "the second key has nothing to grant");

        await using var db = await NewDbAsync(ct);

        var spareSurvives = await db.Items.AnyAsync(x => x.Id == spareKey, ct);
        var ownerships    = await db.CosmeticOwnerships.CountAsync(x => x.UserId == userId && x.CosmeticItemId == cosmeticId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(spareSurvives, Is.True, "a key that granted nothing must still be in the inventory");
            Assert.That(ownerships, Is.EqualTo(1), "and it must not have written a second row either");
        });
    }

    /// <summary>
    /// A key naming a cosmetic the catalogue does not have grants nothing and is not spent.
    /// </summary>
    /// <remarks>
    /// The one refusal an operator can produce by hand: deleting a catalogue row while keys for it are
    /// already sitting in people's inventories. What that has to leave behind is an unspendable key
    /// rather than a destroyed one — put the row back and every one of them works again, and until
    /// then nobody has lost anything.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_key_to_a_cosmetic_that_is_gone_is_refused_and_keeps_the_item(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        // Minted and never inserted, which is the state a key is left in by a catalogue row going away
        // underneath it.
        var keyId = await SeedKeyAsync(userId, ArgonId.New(), null, ct);

        Assert.That(await UseAsync(keyId, ct), Is.False, "there is nothing to hand over");

        await using var db = await NewDbAsync(ct);

        Assert.That(await db.Items.AnyAsync(x => x.Id == keyId, ct), Is.True,
            "a key that granted nothing must still be in the inventory");
    }

    /// <summary>
    /// A key spent on something that has lapsed carries the row forward and records itself as the
    /// reason it is owned again.
    /// </summary>
    /// <remarks>
    /// Nothing else in the codebase can prove this. The other caller is the operator console, which
    /// always passes <c>OperatorGrant</c> and no item, so a revival that failed to relabel the row
    /// would look identical to one that worked — and the console would go on naming an operator who
    /// handed over something that has since lapsed, with the key actually spent recorded nowhere.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_key_reviving_a_lapsed_grant_says_it_is_why(CancellationToken ct = default)
    {
        var userId     = await RegisterUserAsync(ct);
        var cosmeticId = await SeedCosmeticAsync(ct);

        await using (var seed = await NewDbAsync(ct))
        {
            seed.CosmeticOwnerships.Add(new CosmeticOwnershipEntity
            {
                Id              = ArgonId.New(),
                UserId          = userId,
                CosmeticItemId  = cosmeticId,
                Source          = CosmeticOwnershipSource.OperatorGrant,
                InventoryItemId = null,
                ExpiresAt       = DateTimeOffset.UtcNow.AddDays(-1)
            });

            await seed.SaveChangesAsync(ct);
        }

        var keyId = await SeedKeyAsync(userId, cosmeticId, 30, ct);

        Assert.That(await UseAsync(keyId, ct), Is.True, "a lapsed row is not a duplicate");

        await using var db = await NewDbAsync(ct);

        var revived = await db.CosmeticOwnerships
           .SingleAsync(x => x.UserId == userId && x.CosmeticItemId == cosmeticId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(revived.Source, Is.EqualTo(CosmeticOwnershipSource.Item), "the key is why it is owned now");
            Assert.That(revived.InventoryItemId, Is.EqualTo(keyId));
            Assert.That(revived.ExpiresAt, Is.EqualTo(DateTimeOffset.UtcNow.AddDays(30)).Within(5).Minutes,
                "and its time starts again from now, rather than from the date it lapsed on");
        });
    }

    /// <summary>
    /// A key for a cosmetic that is not currently servable grants nothing and is not spent.
    /// </summary>
    /// <remarks>
    /// The state an operator reaches by unpublishing a cosmetic — or that an <c>AvailableUntil</c>
    /// simply arrives at — while keys for it are already in people's inventories. Without this the
    /// key is consumed, a real ownership row is written, and the thing can never be equipped or even
    /// seen in the catalogue, because both of those read the same four conditions. Spending is the
    /// player giving something up, so it has to be for something they can actually wear.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_key_for_an_unpublished_cosmetic_is_refused_and_keeps_the_item(CancellationToken ct = default)
    {
        var userId     = await RegisterUserAsync(ct);
        var cosmeticId = await SeedCosmeticAsync(ct, published: false);
        var keyId      = await SeedKeyAsync(userId, cosmeticId, null, ct);

        Assert.That(await UseAsync(keyId, ct), Is.False, "there is nothing wearable to hand over");

        await using var db = await NewDbAsync(ct);

        var keySurvives = await db.Items.AnyAsync(x => x.Id == keyId, ct);
        var granted     = await db.CosmeticOwnerships.AnyAsync(x => x.UserId == userId && x.CosmeticItemId == cosmeticId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(keySurvives, Is.True, "a key that granted nothing must still be in the inventory");
            Assert.That(granted, Is.False, "and no ownership row may exist for something unwearable");
        });
    }

    /// <summary>
    /// An operator granting that same unpublished cosmetic by hand still succeeds.
    /// </summary>
    /// <remarks>
    /// The refusal above is about intent, not about the row. Handing something out before it is
    /// published is a deliberate act an operator is allowed — a creator gets their badge early — and
    /// they are giving nothing up to do it. Without this test the fix reads as a blanket restriction
    /// on unpublished rows, and the next person to touch it would have no way to tell which of the
    /// two behaviours was the intended one.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task An_operator_grant_of_an_unpublished_cosmetic_still_succeeds(CancellationToken ct = default)
    {
        var userId     = await RegisterUserAsync(ct);
        var cosmeticId = await SeedCosmeticAsync(ct, published: false);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var granted = await admin.GrantCosmetic(userId, cosmeticId, null, ct);

        Assert.That(granted.success, Is.True, granted.error);

        await using var db = await NewDbAsync(ct);

        Assert.That(await db.CosmeticOwnerships.AnyAsync(x => x.UserId == userId && x.CosmeticItemId == cosmeticId, ct),
            Is.True, "an operator may hand out a thing before it is published");
    }

    // ── Authoring a key template ────────────────────────────────────────────────────────────────
    //
    // The four tests above spend a key that was seeded straight into the database. These two cover
    // the other end: the console call that is the only way an operator actually gets one into the
    // catalogue. They need an operator context that nothing above this point required, so
    // SeedOperator and Admin() are copied from CosmeticAdminTests — the established way to reach
    // IAdminConsole in this suite — under a different OperatorId so the two fixtures cannot collide
    // when a shard runs both.

    /// <summary>
    /// The console side of the exchange: naming a cosmetic and a duration on the template is what
    /// makes <see cref="Using_a_key_grants_its_cosmetic"/> possible in the first place.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Creating_a_key_template_records_which_cosmetic_it_opens(CancellationToken ct = default)
    {
        var cosmeticId = await SeedCosmeticAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var created = await admin.CreateItemTemplate(new CreateItemTemplateInput(
            $"cosmetic_key_{Guid.NewGuid():N}", true, true, false, null,
            ItemScenarioKind.Cosmetic, new IonArray<string>([]), cosmeticId, 30), ct);

        Assert.That(created.success, Is.True, created.error);

        await using var db = await NewDbAsync(ct);

        var template = await db.Items
           .Include(x => x.Scenario)
           .FirstOrDefaultAsync(x => x.Id == created.itemId!.Value, ct);

        Assert.That(template?.Scenario, Is.InstanceOf<CosmeticScenario>());

        var scenario = (CosmeticScenario)template!.Scenario!;

        Assert.Multiple(() =>
        {
            Assert.That(scenario.CosmeticId, Is.EqualTo(cosmeticId));
            Assert.That(scenario.DurationDays, Is.EqualTo(30));
        });
    }

    /// <summary>
    /// A template cannot promise a cosmetic the catalogue does not have. Held here, at the moment the
    /// template is authored, rather than only at the moment a key is spent.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_key_to_a_cosmetic_that_does_not_exist_is_refused(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var missingCosmeticId = Guid.NewGuid();

        var created = await admin.CreateItemTemplate(new CreateItemTemplateInput(
            $"cosmetic_key_{Guid.NewGuid():N}", true, true, false, null,
            ItemScenarioKind.Cosmetic, new IonArray<string>([]), missingCosmeticId, null), ct);

        Assert.Multiple(() =>
        {
            Assert.That(created.success, Is.False);
            Assert.That(created.error, Does.Contain(missingCosmeticId.ToString()));
        });
    }

    /// <summary>
    /// The listing is where an operator finds the key they just made, and where a box template
    /// names which templates it drops — a key that reported itself as scenario "None" there would be
    /// the console lying about what the thing is, not merely missing a label.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_key_template_lists_itself_as_a_key(CancellationToken ct = default)
    {
        var cosmeticId = await SeedCosmeticAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var templateId = $"cosmetic_key_{Guid.NewGuid():N}";

        var created = await admin.CreateItemTemplate(new CreateItemTemplateInput(
            templateId, true, true, false, null,
            ItemScenarioKind.Cosmetic, new IonArray<string>([]), cosmeticId, 30), ct);

        Assert.That(created.success, Is.True, created.error);

        var templates = await admin.GetItemTemplates(ct);
        var listed = templates.templates.Values.FirstOrDefault(t => t.templateId == templateId);

        Assert.That(listed, Is.Not.Null);
        Assert.That(listed!.scenarioType, Is.EqualTo(ItemScenarioKind.Cosmetic));
    }

    // ── Guarding the two links back to a cosmetic ───────────────────────────────────────────────
    //
    // GrantItemTemplateId (free text on the catalogue row) and CosmeticScenario.CosmeticId (a plain
    // Guid column on the scenario) each point at the other table with no foreign key backing either
    // direction — the first because it is typed by an operator, the second because a constraint
    // from the inventory's scenario table into the cosmetics catalogue would put one feature's rule
    // inside another's table. These three tests are what stands in for that constraint.

    /// <summary>
    /// <see cref="CosmeticItemEntity.GrantItemTemplateId"/> is a free string; nothing about the
    /// column stops it naming a template that was never created. Caught here, at the moment it is
    /// set, rather than the console showing it back as if it were wired up.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_grant_item_template_naming_nothing_is_refused(CancellationToken ct = default)
    {
        var cosmeticId = await SeedCosmeticAsync(ct);
        var missingTemplateId = $"missing_{Guid.NewGuid():N}";

        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.SetCosmeticAcquisition(new CosmeticAcquisitionInput(
            cosmeticId, new IonArray<CosmeticAcquisitionKind>([CosmeticAcquisitionKind.OperatorGrant]),
            null, null, missingTemplateId), ct);

        Assert.Multiple(() =>
        {
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain(missingTemplateId));
        });
    }

    /// <summary>
    /// A cosmetic with a live key template pointing at it cannot be deleted — the drop would go on
    /// minting keys for something the catalogue no longer has, which is exactly the state
    /// <see cref="A_key_to_a_cosmetic_that_is_gone_is_refused_and_keeps_the_item"/> exists to cover
    /// for a key that already escaped this guard.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_cosmetic_with_a_key_template_pointing_at_it_cannot_be_deleted(CancellationToken ct = default)
    {
        // Unpublished, because deletion refuses a published row first and this test is about the
        // refusal that comes after that one.
        var cosmeticId = await SeedCosmeticAsync(ct, published: false);
        var templateId = $"cosmetic_key_{Guid.NewGuid():N}";

        var (scope, admin) = Admin();
        await using var _ = scope;

        var created = await admin.CreateItemTemplate(new CreateItemTemplateInput(
            templateId, true, true, false, null,
            ItemScenarioKind.Cosmetic, new IonArray<string>([]), cosmeticId, null), ct);

        Assert.That(created.success, Is.True, created.error);

        var deleted = await admin.DeleteCosmetic(cosmeticId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(deleted.success, Is.False);
            Assert.That(deleted.error, Does.Contain(templateId));
        });
    }

    /// <summary>
    /// Once the template naming it is gone, the same cosmetic deletes cleanly — otherwise the guard
    /// above would be a trap rather than a rule, refusing forever with nothing an operator could do
    /// about it.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task The_same_cosmetic_can_be_deleted_once_that_template_is_gone(CancellationToken ct = default)
    {
        var cosmeticId = await SeedCosmeticAsync(ct, published: false);
        var templateId = $"cosmetic_key_{Guid.NewGuid():N}";

        var (scope, admin) = Admin();
        await using var _ = scope;

        var created = await admin.CreateItemTemplate(new CreateItemTemplateInput(
            templateId, true, true, false, null,
            ItemScenarioKind.Cosmetic, new IonArray<string>([]), cosmeticId, null), ct);

        Assert.That(created.success, Is.True, created.error);

        var templateDeleted = await admin.DeleteItemTemplate(created.itemId!.Value, ct);

        Assert.That(templateDeleted.success, Is.True, templateDeleted.error);

        var deleted = await admin.DeleteCosmetic(cosmeticId, ct);

        Assert.That(deleted.success, Is.True, deleted.error);
    }

    /// <summary>
    /// A write through the acquisition wire leaves the flags that wire cannot name alone.
    /// </summary>
    /// <remarks>
    /// <c>Free</c> has no member on <c>CosmeticAcquisitionKind</c>, so the console cannot send it and
    /// an assignment of the whole mode would read its silence as "take it away". The key dialog calls
    /// this on every save, so making a key for a free cosmetic would clear the one flag
    /// <c>CosmeticsGrain.Owns</c> and <c>CosmeticProfileProjection</c> check before ownership — and
    /// the thing would stop rendering for everyone already wearing it.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Setting_acquisition_leaves_a_free_cosmetic_free(CancellationToken ct = default)
    {
        var cosmeticId = await SeedCosmeticAsync(ct);

        await using (var seed = await NewDbAsync(ct))
        {
            var row = await seed.Cosmetics.FirstAsync(x => x.Id == cosmeticId, ct);

            row.AcquisitionMode = CosmeticAcquisitionMode.Free | CosmeticAcquisitionMode.OperatorGrant;

            await seed.SaveChangesAsync(ct);
        }

        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.SetCosmeticAcquisition(new CosmeticAcquisitionInput(
            cosmeticId,
            new IonArray<CosmeticAcquisitionKind>([CosmeticAcquisitionKind.OperatorGrant, CosmeticAcquisitionKind.Drop]),
            null, null, null), ct);

        Assert.That(result.success, Is.True, result.error);

        await using var db = await NewDbAsync(ct);

        var stored = await db.Cosmetics.FirstAsync(x => x.Id == cosmeticId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stored.AcquisitionMode.HasFlag(CosmeticAcquisitionMode.Free), Is.True,
                "a wire that cannot say Free cannot be asked to remove it");
            Assert.That(stored.AcquisitionMode.HasFlag(CosmeticAcquisitionMode.Drop), Is.True,
                "and the write it could express still has to happen");
        });
    }
}
