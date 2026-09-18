namespace ArgonComplexTest.Tests;

using Argon.Api.Entities.Data;
using Argon.Entities;
using Argon.Features.Admin;
using ConsoleContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The operator surface for profile cosmetics, driven the way the console drives it.
/// </summary>
/// <remarks>
/// <para>Direct against <c>IAdminConsole</c> rather than over its Ion port, for the reason
/// <see cref="AdminConsoleTests"/> gives: the port is guarded by an interceptor whose only output is
/// the ambient <see cref="OperatorRequestContext"/>, and setting that reproduces the state every
/// method runs under.</para>
///
/// <para><b>This fixture is load-bearing in a way the others are not.</b> The admin console's
/// frontend lives in a repository this one does not contain, so there is no way to click any of
/// this; these tests are the only thing that has ever exercised the cosmetics console at all.</para>
///
/// <para>Nothing here uploads to object storage. What it publishes is a badge whose picture is
/// declared to be its wearer's, which needs no catalogue asset — that is what makes publication
/// reachable in two calls instead of behind a working S3 round trip. The asset rules have their own
/// coverage in the unit suite (<c>CosmeticPublicationTests</c>).</para>
/// </remarks>
[TestFixture]
public class CosmeticAdminTests : TestBase
{
    private static readonly Guid OperatorId = Guid.Parse("00000000-0000-0000-0000-0000000ad002");

    [OneTimeSetUp]
    public async Task SeedOperator()
    {
        await using var db = await NewDbAsync(CancellationToken.None);

        if (await db.Operators.AnyAsync(o => o.Id == OperatorId, CancellationToken.None))
            return;

        db.Operators.Add(new OperatorEntity
        {
            Id               = OperatorId,
            DisplayName      = "Cosmetics Test Operator",
            Email            = "cosmetics-operator@argon.test",
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
            Email                 = "cosmetics-operator@argon.test",
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

    /// <summary>
    /// A badge whose picture is its wearer's, which is how a row publishes with no file behind it.
    /// </summary>
    /// <remarks>
    /// This used to be a nickname style, back when that kind had rows. It carries no required asset
    /// either way, which is what keeps publication reachable in two calls rather than behind a
    /// working object-storage round trip.
    /// </remarks>
    private static CreateCosmeticInput Badge(string slug)
        => new("profile.badge", slug, $"cosmetic_{slug}", null, "rare", 0,
            $$"""{"tooltipKey":"cosmetic_{{slug}}"}""", CosmeticAssetSourceKind.UserProvided,
            $"Badge {slug}", null);

    private static async Task<Guid> CreateStyleAsync(IAdminConsole admin, string slug, CancellationToken ct)
        => await CreateAsync(admin, Badge(slug), ct);

    private static async Task<Guid> CreateAsync(IAdminConsole admin, CreateCosmeticInput input, CancellationToken ct)
    {
        var created = await admin.CreateCosmetic(input, ct);

        Assert.That(created.success, Is.True, created.error);
        Assert.That(created.cosmeticId, Is.Not.Null);

        return created.cosmeticId!.Value;
    }

    private static string UniqueSlug() => $"t{Guid.NewGuid():N}"[..12];

    // ── The kinds this build ships ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The console reports the kinds the assembly declares, and cannot create one.
    /// </summary>
    /// <remarks>
    /// The list is exact rather than a set of "at least these", because a kind is one file and the
    /// whole promise of that is that deleting the file deletes the kind. An exact list is what makes
    /// a file deleted by accident — or a kind added without anybody noticing — show up here.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task GetCosmeticKinds_ReportsWhatTheBuildDeclares(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var kinds = await admin.GetCosmeticKinds(ct);
        var keys  = kinds.kinds.Select(k => k.kindKey).ToArray();

        Assert.That(keys, Is.EquivalentTo(new[]
        {
            "profile.background", "profile.badge", "avatar.decoration", "nickname.style",
            "widget.note", "widget.tags", "widget.picture",

            // Over the card rather than under it, which is what makes them kinds of their own and
            // not two more backgrounds.
            "profile.frame", "profile.effect",

            // A list of moving things with a depth each, which is the one shape an effect cannot
            // hold: an effect is one picture over the card, and no number added to one picture makes
            // it two figures on different paths on different sides of the card's own text.
            "profile.scene",

            // Bodies with a depth rather than a layer over a face, which is what makes this one a
            // kind of its own and not a second avatar decoration: the face hides the half of the
            // circuit that passes behind it, and no layer is on two sides of anything.
            "avatar.orbit",

            // The axes a nickname style is composed from. They are kinds like any other — their rows
            // are what the picker offers on each axis — and they are the reason there is one style
            // row here rather than one per combination of face, colour and treatment.
            "option.font", "option.swatch", "option.text-effect"
        }));
    }

    /// <summary>
    /// A new profile frame is a row in the catalogue and nothing else.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the promise the whole design is for, written down.</b> An operator adds a
    /// frame from the console — four numbers and publish — and everybody sees it on their next
    /// catalogue read. No file to upload, no client to ship, no release: the client already knows
    /// how to draw a border, and which borders exist is a question for the database.</para>
    ///
    /// <para>It is the frame rather than one of the others because a frame is the case with nothing
    /// left over: the payload is a width, some colours, an angle and a glow, so there is not even an
    /// asset to put in object storage first.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_new_frame_needs_no_file_and_no_release(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var slug = UniqueSlug();

        var id = await CreateAsync(admin, new CreateCosmeticInput(
            "profile.frame", slug, $"cosmetic_{slug}", null, "rare", 0,
            """{"parts":[{"type":"ring","thickness":3,"colors":[-16711681,-65281],"angle":90,"glow":0.5}]}""",
            CosmeticAssetSourceKind.Catalogue, $"Frame {slug}", null), ct);

        var published = await admin.PublishCosmetic(id, ct);

        Assert.That(published.success, Is.True, published.detail);

        var details = await admin.GetCosmetic(id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(details.isPublished, Is.True);
            Assert.That(details.assets, Is.Empty, "a frame is numbers, so there is no file behind it");
        });
    }

    /// <summary>
    /// A payload the kind's own rules refuse does not become a row.
    /// </summary>
    /// <remarks>
    /// The other half of letting an operator author a frame from a console: what they write is held
    /// to the kind's schema at the door, because it ends up in a style on everybody who opens that
    /// person's profile.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_frame_with_no_colours_is_refused(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var created = await admin.CreateCosmetic(new CreateCosmeticInput(
            "profile.frame", UniqueSlug(), "n", null, null, 0,
            """{"parts":[{"type":"ring","thickness":3,"colors":[],"angle":90,"glow":0}]}""",
            CosmeticAssetSourceKind.Catalogue, null, null), ct);

        Assert.That(created.success, Is.False);
        Assert.That(created.error, Does.Contain("colors"));
    }

    /// <summary>
    /// A kind nobody has switched is on. See <c>CosmeticKindGate</c> for why absence means enabled.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_kind_with_no_flag_row_is_enabled(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var kinds  = await admin.GetCosmeticKinds(ct);
        var badge  = kinds.kinds.First(k => k.kindKey == "profile.badge");

        Assert.Multiple(() =>
        {
            Assert.That(badge.isEnabled, Is.True);
            Assert.That(badge.featureFlagKey, Is.EqualTo("af.cosmetics.profile-badge.active"));
        });
    }

    /// <summary>
    /// The switch the whole design is for: one call and the kind stops existing for users.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task SetCosmeticKindEnabled_TurnsAKindOffAndBackOn(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var off = await admin.SetCosmeticKindEnabled("avatar.decoration", false, ct);
        Assert.That(off.success, Is.True, off.error);

        var afterOff = await admin.GetCosmeticKinds(ct);
        Assert.That(afterOff.kinds.First(k => k.kindKey == "avatar.decoration").isEnabled, Is.False);

        var on = await admin.SetCosmeticKindEnabled("avatar.decoration", true, ct);
        Assert.That(on.success, Is.True, on.error);

        var afterOn = await admin.GetCosmeticKinds(ct);
        Assert.That(afterOn.kinds.First(k => k.kindKey == "avatar.decoration").isEnabled, Is.True);
    }

    [Test, CancelAfter(120_000)]
    public async Task SetCosmeticKindEnabled_RefusesAKindThisBuildDoesNotHave(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.SetCosmeticKindEnabled("profile.aurora", false, ct);

        Assert.Multiple(() =>
        {
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("profile.aurora"));
        });
    }

    /// <summary>
    /// Purging is for keys no file declares. Against a live kind it is "delete everything of this
    /// kind", so it refuses.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task PurgeOrphanedCosmetics_RefusesALiveKind(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.PurgeOrphanedCosmetics("profile.badge", ct);

        Assert.Multiple(() =>
        {
            Assert.That(result.success, Is.False);
            Assert.That(result.error, Does.Contain("declared in this build"));
        });
    }

    // ── The catalogue ───────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task CreateCosmetic_RefusesAKindThisBuildDoesNotHave(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.CreateCosmetic(
            new CreateCosmeticInput("profile.aurora", UniqueSlug(), "n", null, null, 0, "{}",
                CosmeticAssetSourceKind.Catalogue, null, null), ct);

        Assert.That(result.success, Is.False);
    }

    /// <summary>
    /// The payload is checked against the kind's own type before the row is written, so a draft is
    /// never stored in a shape its renderer cannot read.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task CreateCosmetic_RefusesAPayloadItsKindRejects(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.CreateCosmetic(
            new CreateCosmeticInput("profile.badge", UniqueSlug(), "n", null, null, 0,
                """{"weight":9000}""", CosmeticAssetSourceKind.Catalogue, null, null), ct);

        Assert.That(result.success, Is.False);
    }

    /// <summary>
    /// A rarity outside the client's vocabulary is refused rather than stored. A key item's rarity is
    /// what the loot-grant animation on the client uses to choose a colour — see <c>ItemQuality</c>
    /// in <c>client/packages/inventory/src/index.ts</c> — and a value that type does not recognise
    /// falls back to a grey render with no error raised anywhere else in the chain. This is the only
    /// point where a typo like this can still be caught.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_rarity_the_client_cannot_draw_is_refused(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var slug = UniqueSlug();

        var created = await admin.CreateCosmetic(new CreateCosmeticInput(
            "profile.badge", slug, $"cosmetic_{slug}", null, "mythic", 0,
            $$"""{"tooltipKey":"cosmetic_{{slug}}"}""", CosmeticAssetSourceKind.UserProvided,
            null, null), ct);

        Assert.Multiple(() =>
        {
            Assert.That(created.success, Is.False);
            Assert.That(created.error, Does.Contain("mythic"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task CreateCosmetic_RefusesASlugTheKindAlreadyUses(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var slug = UniqueSlug();
        await CreateStyleAsync(admin, slug, ct);

        var again = await admin.CreateCosmetic(Badge(slug), ct);

        Assert.Multiple(() =>
        {
            Assert.That(again.success, Is.False);
            Assert.That(again.error, Does.Contain(slug));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_new_cosmetic_starts_enabled_and_unpublished(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id      = await CreateStyleAsync(admin, UniqueSlug(), ct);
        var details = await admin.GetCosmetic(id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(details.isEnabled, Is.True);
            Assert.That(details.isPublished, Is.False);
            Assert.That(details.payloadIsValid, Is.True);
        });
    }

    /// <summary>
    /// An operator who types a rarity in the wrong case is not refused, but the row does not keep
    /// their casing either: it is stored exactly as the client's vocabulary spells it, so the
    /// client's own case-insensitive comparison is never what makes this work.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_rarity_typed_in_the_wrong_case_is_stored_in_the_clients_casing(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var slug = UniqueSlug();

        var id = await CreateAsync(admin, new CreateCosmeticInput(
            "profile.badge", slug, $"cosmetic_{slug}", null, "Legendary", 0,
            $$"""{"tooltipKey":"cosmetic_{{slug}}"}""", CosmeticAssetSourceKind.UserProvided,
            null, null), ct);

        var details = await admin.GetCosmetic(id, ct);

        Assert.That(details.rarity, Is.EqualTo("legendary"));
    }

    // ── The gate ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Publishing_twice_is_refused(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateStyleAsync(admin, UniqueSlug(), ct);

        await admin.PublishCosmetic(id, ct);

        var again = await admin.PublishCosmetic(id, ct);

        Assert.That(again.error, Is.EqualTo(PublishCosmeticError.AlreadyPublished));
    }

    // ── Holdings ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task GrantAndRevoke_MoveAnAccountsHolding(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateStyleAsync(admin, UniqueSlug(), ct);

        var granted = await admin.GrantCosmetic(userId, id, null, ct);
        Assert.That(granted.success, Is.True, granted.error);

        var afterGrant = await admin.GetUserCosmetics(userId, ct);
        var held       = afterGrant.items.FirstOrDefault(x => x.cosmeticId == id);

        Assert.Multiple(() =>
        {
            Assert.That(held, Is.Not.Null);
            Assert.That(held!.isActive, Is.True);
            Assert.That(held.source, Is.EqualTo(CosmeticOwnershipSourceKind.OperatorGrant));
            Assert.That(held.viaSubscription, Is.False);
        });

        // Granting again is how a timed grant is extended, so it must not fail or duplicate.
        var twice = await admin.GrantCosmetic(userId, id, DateTimeOffset.UtcNow.AddDays(30), ct);
        Assert.That(twice.success, Is.True, twice.error);
        Assert.That((await admin.GetUserCosmetics(userId, ct)).items.Count(x => x.cosmeticId == id), Is.EqualTo(1));

        var revoked = await admin.RevokeCosmetic(userId, id, ct);
        Assert.That(revoked.success, Is.True, revoked.error);

        var afterRevoke = (await admin.GetUserCosmetics(userId, ct)).items.First(x => x.cosmeticId == id);

        Assert.Multiple(() =>
        {
            // Kept rather than deleted: a revocation is a record of what was taken and when.
            Assert.That(afterRevoke.isActive, Is.False);
            Assert.That(afterRevoke.revokedAt, Is.Not.Null);
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task RevokingSomethingNobodyHoldsSaysSo(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var id     = await CreateStyleAsync(admin, UniqueSlug(), ct);
        var result = await admin.RevokeCosmetic(userId, id, ct);

        Assert.That(result.success, Is.False);
    }

    // ── Deletion ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task DeleteCosmetic_RefusesWhilePublished(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateStyleAsync(admin, UniqueSlug(), ct);

        await admin.PublishCosmetic(id, ct);

        var deleted = await admin.DeleteCosmetic(id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(deleted.success, Is.False);
            Assert.That(deleted.error, Does.Contain("Unpublish"));
        });
    }

    /// <summary>
    /// Deleting something accounts hold is taking property off them, not editing a catalogue.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task DeleteCosmetic_RefusesWhileSomebodyHoldsIt(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateStyleAsync(admin, UniqueSlug(), ct);
        await admin.GrantCosmetic(userId, id, null, ct);

        var deleted = await admin.DeleteCosmetic(id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(deleted.success, Is.False);
            Assert.That(deleted.error, Does.Contain("hold it"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteCosmetic_RemovesAnUnheldDraft(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id      = await CreateStyleAsync(admin, UniqueSlug(), ct);
        var deleted = await admin.DeleteCosmetic(id, ct);

        Assert.That(deleted.success, Is.True, deleted.error);
    }

    // ── Search ──────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task SearchCosmetics_FindsBySlugWithinAKind(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var slug = UniqueSlug();
        var id   = await CreateStyleAsync(admin, slug, ct);

        var page = await admin.SearchCosmetics(
            new CosmeticQuery("profile.badge", slug, false, false, 0, 50, false), ct);

        Assert.Multiple(() =>
        {
            Assert.That(page.totalCount, Is.EqualTo(1));
            Assert.That(page.items.Single().cosmeticId, Is.EqualTo(id));
        });
    }

    /// <summary>
    /// A row is findable by what it is called, in any language it has been called it.
    /// </summary>
    /// <remarks>
    /// The slug and the key are what an engineer would type; an operator looking for a cosmetic
    /// knows the word a person reads. The name also comes back on the summary, so a list reads as a
    /// person would say it — asserted here because nothing else covers the search path's own
    /// projection.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task SearchCosmetics_FindsARowByItsTranslatedName(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var slug = UniqueSlug();

        var id = await CreateAsync(admin, new CreateCosmeticInput(
            "profile.background", slug, "", null, null, 0,
            """{"loop":true,"tintOpacity":0.3}""", CosmeticAssetSourceKind.Catalogue,
            "Northern Lights", null), ct);

        var russian = $"Северное сияние {slug}";

        Assert.That((await admin.SetCosmeticTranslation(
            new CosmeticTranslationInput(id, "ru", russian, null), ct)).success, Is.True);

        var page = await admin.SearchCosmetics(
            new CosmeticQuery("profile.background", russian, false, false, 0, 50, false), ct);

        Assert.Multiple(() =>
        {
            Assert.That(page.items.Select(x => x.cosmeticId), Does.Contain(id));
            Assert.That(page.items.Single(x => x.cosmeticId == id).name, Is.EqualTo("Northern Lights"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task SearchCosmetics_OnlyPublished_HidesADraft(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var slug = UniqueSlug();
        await CreateStyleAsync(admin, slug, ct);

        var page = await admin.SearchCosmetics(
            new CosmeticQuery("profile.badge", slug, true, false, 0, 50, false), ct);

        Assert.That(page.totalCount, Is.Zero);
    }

    // ── Rows whose files went into a client build ───────────────────────────────────────────────

    /// <summary>
    /// A published row, marked as having shipped in a build.
    /// </summary>
    /// <remarks>
    /// Marking is what the console does <b>after</b> a release is out. Everything below is about
    /// what an operator may then still do to the row, and the answer in every case is governed by
    /// one fact: the files are inside applications that are already running, and no call here can
    /// reach them.
    /// </remarks>
    private static async Task<Guid> CreateShippedAsync(IAdminConsole admin, CancellationToken ct)
    {
        var id = await CreateStyleAsync(admin, UniqueSlug(), ct);

        var published = await admin.PublishCosmetic(id, ct);
        Assert.That(published.success, Is.True, published.detail);

        var shipped = await admin.SetCosmeticShipped(id, "1.4.0", true, ct);
        Assert.That(shipped.success, Is.True, shipped.error);

        return id;
    }

    [Test, CancelAfter(120_000)]
    public async Task Only_a_published_cosmetic_can_be_marked_shipped(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id     = await CreateStyleAsync(admin, UniqueSlug(), ct);
        var marked = await admin.SetCosmeticShipped(id, "1.4.0", true, ct);

        Assert.Multiple(() =>
        {
            Assert.That(marked.success, Is.False);
            Assert.That(marked.error, Does.Contain("Publish it first"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Marking_shipped_needs_the_build_it_shipped_in(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateStyleAsync(admin, UniqueSlug(), ct);
        await admin.PublishCosmetic(id, ct);

        var marked = await admin.SetCosmeticShipped(id, "   ", true, ct);

        Assert.Multiple(() =>
        {
            Assert.That(marked.success, Is.False);
            Assert.That(marked.error, Does.Contain("Name the client build"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_shipped_cosmetic_says_so_on_both_the_row_and_the_page(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id      = await CreateShippedAsync(admin, ct);
        var details = await admin.GetCosmetic(id, ct);

        var page = await admin.SearchCosmetics(
            new CosmeticQuery("profile.badge", details.slug, false, false, 0, 50, false), ct);

        Assert.Multiple(() =>
        {
            Assert.That(details.shippedInClientAt, Is.Not.Null);
            Assert.That(details.shippedInClientBuild, Is.EqualTo("1.4.0"));
            Assert.That(page.items.Single().shippedInClientBuild, Is.EqualTo("1.4.0"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_shipped_cosmetic_cannot_be_switched_off(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id       = await CreateShippedAsync(admin, ct);
        var disabled = await admin.SetCosmeticEnabled(id, false, ct);

        Assert.Multiple(() =>
        {
            Assert.That(disabled.success, Is.False);
            Assert.That(disabled.error, Does.Contain("1.4.0"));
        });

        // Switching it back on is a row returning to the state its own files assume, so it is not
        // refused — and there is no way to reach that from a refusal alone.
        var enabled = await admin.SetCosmeticEnabled(id, true, ct);

        Assert.That(enabled.success, Is.True, enabled.error);
    }

    [Test, CancelAfter(120_000)]
    public async Task A_shipped_cosmetic_cannot_be_unpublished(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id          = await CreateShippedAsync(admin, ct);
        var unpublished = await admin.UnpublishCosmetic(id, "changed my mind", ct);

        Assert.Multiple(() =>
        {
            Assert.That(unpublished.success, Is.False);
            Assert.That(unpublished.error, Does.Contain("builds people are running"));
        });
    }

    /// <summary>
    /// Purging a vanished kind does not take a shipped row with it.
    /// </summary>
    /// <remarks>
    /// The kind's file being gone does not mean the bytes are: a shipped row's files are inside
    /// applications people are running, and this is the only route left that would delete such a
    /// row — and everyone's ownership of it — without ever mentioning it.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Purging_a_kind_will_not_take_a_shipped_row_with_it(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateShippedAsync(admin, ct);

        // The kind is still declared by this build, so purge refuses on that first. Make the row
        // look like an orphan the only way a test can: move it under a key no file declares.
        await using (var db = await NewDbAsync(ct))
        {
            var row = await db.Cosmetics.FirstAsync(x => x.Id == id, ct);

            row.KindKey = "profile.gone";
            await db.SaveChangesAsync(ct);
        }

        var purged = await admin.PurgeOrphanedCosmetics("profile.gone", ct);

        await using var after = await NewDbAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(purged.success, Is.False);
            Assert.That(purged.error, Does.Contain("shipped in a client build"));
            Assert.That(after.Cosmetics.Any(x => x.Id == id), Is.True);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_shipped_cosmetic_cannot_be_deleted(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id      = await CreateShippedAsync(admin, ct);
        var deleted = await admin.DeleteCosmetic(id, ct);

        // Not "unpublish it first": that door is locked too, and an answer that sends an operator at
        // it is an answer that wastes their next five minutes.
        Assert.Multiple(() =>
        {
            Assert.That(deleted.success, Is.False);
            Assert.That(deleted.error, Does.Contain("shipped in client build"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_shipped_cosmetic_cannot_be_given_an_availability_window(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateShippedAsync(admin, ct);

        var windowed = await admin.UpdateCosmetic(new UpdateCosmeticInput(
            id, null, null, null, null, null,
            null, DateTimeOffset.UtcNow.AddDays(7), false, false), ct);

        Assert.Multiple(() =>
        {
            Assert.That(windowed.success, Is.False);
            Assert.That(windowed.error, Does.Contain("availability window"));
        });
    }

    /// <summary>
    /// A row with a window is refused, and the window is still there afterwards.
    /// </summary>
    /// <remarks>
    /// A window on something permanent is a contradiction, so it has to be resolved — but clearing
    /// it here would destroy the only record of what the dates were, and unmarking could not put
    /// them back. The operator who marked the wrong row would have nothing to recover from.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Marking_shipped_is_refused_while_a_window_is_set(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id    = await CreateStyleAsync(admin, UniqueSlug(), ct);
        var until = DateTimeOffset.UtcNow.AddDays(7);

        var windowed = await admin.UpdateCosmetic(new UpdateCosmeticInput(
            id, null, null, null, null, null, null, until, false, false), ct);

        Assert.That(windowed.success, Is.True, windowed.error);

        await admin.PublishCosmetic(id, ct);

        var marked  = await admin.SetCosmeticShipped(id, "1.4.0", true, ct);
        var details = await admin.GetCosmetic(id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(marked.success, Is.False);
            Assert.That(marked.error, Does.Contain("Clear the availability window first"));
            Assert.That(details.availableUntil, Is.Not.Null);
            Assert.That(details.shippedInClientAt, Is.Null);
        });

        // And once the dates are gone by the operator's own hand, it goes through.
        var cleared = await admin.UpdateCosmetic(new UpdateCosmeticInput(
            id, null, null, null, null, null, null, null, true, true), ct);

        Assert.That(cleared.success, Is.True, cleared.error);

        var second = await admin.SetCosmeticShipped(id, "1.4.0", true, ct);

        Assert.That(second.success, Is.True, second.error);
    }

    [Test, CancelAfter(120_000)]
    public async Task Marking_shipped_refuses_a_build_name_too_long_to_store(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateStyleAsync(admin, UniqueSlug(), ct);
        await admin.PublishCosmetic(id, ct);

        var marked = await admin.SetCosmeticShipped(id, new string('v', 65), true, ct);

        Assert.Multiple(() =>
        {
            Assert.That(marked.success, Is.False);
            Assert.That(marked.error, Does.Contain("64 characters"));
        });
    }

    /// <summary>
    /// A slug has to survive being a directory name, because that is one of the things it becomes.
    /// </summary>
    /// <remarks>
    /// The console writes <c>items/{kindKey}/{slug}/</c> into the pack zip. A slug of <c>..</c>
    /// made an ordinary export into an archive that escaped the folder it was unpacked into.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_slug_that_is_not_a_usable_name_is_refused(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        foreach (var slug in new[] { "..", "../../evil", "Upper", "has space", "a", "dot.dot" })
        {
            var created = await admin.CreateCosmetic(Badge(slug), ct);

            Assert.Multiple(() =>
            {
                Assert.That(created.success, Is.False, $"'{slug}' was accepted");
                Assert.That(created.error, Does.Contain("lowercase letters"), $"for '{slug}'");
            });
        }
    }

    /// <summary>
    /// Unmarking gives the row back.
    /// </summary>
    /// <remarks>
    /// The build name is typed in by hand after a release, which is exactly the kind of thing that
    /// gets done to the wrong row. A mistake nobody can undo would be worse than one they can.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Unmarking_shipped_makes_the_row_ordinary_again(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateShippedAsync(admin, ct);

        var unmarked = await admin.SetCosmeticShipped(id, "", false, ct);
        Assert.That(unmarked.success, Is.True, unmarked.error);

        var unpublished = await admin.UnpublishCosmetic(id, "it really was wrong", ct);
        var details     = await admin.GetCosmetic(id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(unpublished.success, Is.True, unpublished.error);
            Assert.That(details.shippedInClientAt, Is.Null);
            Assert.That(details.shippedInClientBuild, Is.Null);
        });
    }

    // ── Codes ───────────────────────────────────────────────────────────────────────────────────

    private static async Task<(Guid CosmeticId, string TemplateId)> CosmeticWithKeyAsync(
        IAdminConsole admin, CancellationToken ct)
    {
        var id         = await CreateStyleAsync(admin, UniqueSlug(), ct);
        var templateId = $"key_{Guid.NewGuid():N}"[..16];

        var created = await admin.CreateItemTemplate(new CreateItemTemplateInput(
            templateId, true, true, false, null, ItemScenarioKind.Cosmetic,
            new IonArray<string>([]), id, null), ct);

        Assert.That(created.success, Is.True, created.error);

        return (id, templateId);
    }

    [Test, CancelAfter(120_000)]
    public async Task Generated_codes_are_single_use_and_all_different(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var (cosmeticId, templateId) = await CosmeticWithKeyAsync(admin, ct);

        var made = await admin.CreateCosmeticCodes(new CreateCosmeticCodesInput(
            cosmeticId, templateId, 25,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), "AUTUMN"), ct);

        Assert.That(made.success, Is.True, made.error);

        var codes = made.codes.ToArray();

        await using var db = await NewDbAsync(ct);

        var stored = await db.Coupons.Where(x => codes.Contains(x.Code)).ToListAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(codes, Has.Length.EqualTo(25));
            Assert.That(codes.Distinct(), Has.Exactly(25).Items);
            Assert.That(codes, Is.All.StartWith("AUTUMN-"));
            Assert.That(stored, Has.Count.EqualTo(25));
            Assert.That(stored, Is.All.Matches<ArgonCouponEntity>(x => x.MaxRedemptions == 1 && x.IsActive));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Generating_codes_says_the_row_can_be_got_by_code(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var (cosmeticId, templateId) = await CosmeticWithKeyAsync(admin, ct);

        await admin.CreateCosmeticCodes(new CreateCosmeticCodesInput(
            cosmeticId, templateId, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), null), ct);

        var details = await admin.GetCosmetic(cosmeticId, ct);

        Assert.That(details.acquisition, Does.Contain(CosmeticAcquisitionKind.PromoCode));
    }

    /// <summary>
    /// A code may only mint this cosmetic's own key.
    /// </summary>
    /// <remarks>
    /// A batch pointed at somebody else's key is a batch nobody can explain once it is in the wild,
    /// and there is no way to recall codes that have already been handed out.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Generating_codes_refuses_a_key_for_another_cosmetic(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var (_, templateId) = await CosmeticWithKeyAsync(admin, ct);
        var other           = await CreateStyleAsync(admin, UniqueSlug(), ct);

        var made = await admin.CreateCosmeticCodes(new CreateCosmeticCodesInput(
            other, templateId, 5,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), null), ct);

        Assert.Multiple(() =>
        {
            Assert.That(made.success, Is.False);
            Assert.That(made.error, Does.Contain("different cosmetic"));
            Assert.That(made.codes, Is.Empty);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Generating_codes_refuses_a_template_that_is_not_a_key(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var cosmeticId = await CreateStyleAsync(admin, UniqueSlug(), ct);
        var templateId = $"box_{Guid.NewGuid():N}"[..16];

        var created = await admin.CreateItemTemplate(new CreateItemTemplateInput(
            templateId, true, true, false, null, ItemScenarioKind.Premium,
            new IonArray<string>([]), null, null), ct);

        Assert.That(created.success, Is.True, created.error);

        var made = await admin.CreateCosmeticCodes(new CreateCosmeticCodesInput(
            cosmeticId, templateId, 5,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), null), ct);

        Assert.Multiple(() =>
        {
            Assert.That(made.success, Is.False);
            Assert.That(made.error, Does.Contain("Make a key"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Generating_codes_refuses_a_batch_nobody_asked_for(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var (cosmeticId, templateId) = await CosmeticWithKeyAsync(admin, ct);

        var tooMany = await admin.CreateCosmeticCodes(new CreateCosmeticCodesInput(
            cosmeticId, templateId, 5000,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), null), ct);

        var backwards = await admin.CreateCosmeticCodes(new CreateCosmeticCodesInput(
            cosmeticId, templateId, 5,
            DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow, null), ct);

        Assert.Multiple(() =>
        {
            Assert.That(tooMany.success, Is.False);
            Assert.That(tooMany.error, Does.Contain("1 and 1000"));
            Assert.That(backwards.success, Is.False);
            Assert.That(backwards.error, Does.Contain("ends before it starts"));
        });
    }

    // ── Names ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A name written in the console comes back from the console, in the language it was written in.
    /// </summary>
    /// <remarks>
    /// The whole point of the table: a cosmetic created today is named today, in a language nobody
    /// had to ship a client release for. <c>am</c> is written in the operator's own casing to prove
    /// the server normalises it rather than storing what was typed.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_translation_round_trips(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var slug = UniqueSlug();

        var id = await CreateAsync(admin, new CreateCosmeticInput(
            "profile.background", slug, "", null, null, 0,
            """{"loop":true,"tintOpacity":0.3}""", CosmeticAssetSourceKind.Catalogue,
            "Sakura", null), ct);

        var set = await admin.SetCosmeticTranslation(new CosmeticTranslationInput(
            id, "AM", "Սակուրա", null), ct);

        Assert.That(set.success, Is.True, set.error);

        var details = await admin.GetCosmetic(id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(details.translations.Select(x => x.locale), Is.EquivalentTo(new[] { "en", "am" }));
            Assert.That(details.translations.First(x => x.locale == "am").name, Is.EqualTo("Սակուրա"));
        });
    }

    /// <summary>
    /// (cosmetic, locale) is the key, so a second write of a language is a correction.
    /// </summary>
    /// <remarks>
    /// The console has no translation id to hold on to — it sends a language and a name — so an add
    /// that collected duplicates would leave two rows with nothing to choose between them.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Writing_a_language_twice_edits_it(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var slug = UniqueSlug();

        var id = await CreateAsync(admin, new CreateCosmeticInput(
            "profile.background", slug, "", null, null, 0,
            """{"loop":true,"tintOpacity":0.3}""", CosmeticAssetSourceKind.Catalogue,
            "Sakura", null), ct);

        var first  = await admin.SetCosmeticTranslation(new CosmeticTranslationInput(id, "ru", "Сакура", null), ct);
        var second = await admin.SetCosmeticTranslation(new CosmeticTranslationInput(id, "ru", "Вишня", "Цветение"), ct);

        Assert.That(first.success, Is.True, first.error);
        Assert.That(second.success, Is.True, second.error);

        var details = await admin.GetCosmetic(id, ct);
        var russian = details.translations.Single(x => x.locale == "ru");

        Assert.Multiple(() =>
        {
            Assert.That(russian.name, Is.EqualTo("Вишня"));
            Assert.That(russian.description, Is.EqualTo("Цветение"));
        });
    }

    /// <summary>
    /// A row nobody has named does not reach a client.
    /// </summary>
    /// <remarks>
    /// The gate that replaces the old silence: a cosmetic used to publish with a name key no locale
    /// file declared, and vue-i18n rendered the key itself with <c>missingWarn: false</c>. Refusing
    /// publication is what turns that into something an operator is told about.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_row_with_no_fallback_name_is_refused_publication(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateAsync(admin, new CreateCosmeticInput(
            "option.font", UniqueSlug(), "", null, null, 0,
            """{"CssFamily":"Nameless, serif"}""", CosmeticAssetSourceKind.Catalogue,
            null, null), ct);

        var published = await admin.PublishCosmetic(id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(published.success, Is.False);
            Assert.That(published.error, Is.EqualTo(PublishCosmeticError.MissingName));
        });
    }

    /// <summary>
    /// The fallback language cannot be taken off a row that is already on screen.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task The_fallback_name_of_a_published_row_cannot_be_deleted(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var id = await CreateAsync(admin, new CreateCosmeticInput(
            "option.font", UniqueSlug(), "", null, null, 0,
            """{"CssFamily":"Gilded, serif"}""", CosmeticAssetSourceKind.Catalogue,
            "Gilded", null), ct);

        var published = await admin.PublishCosmetic(id, ct);

        Assert.That(published.success, Is.True, published.detail);

        var deleted = await admin.DeleteCosmeticTranslation(id, "en", ct);

        Assert.Multiple(() =>
        {
            Assert.That(deleted.success, Is.False);
            Assert.That(deleted.error, Does.Contain("published"));
        });
    }
}
