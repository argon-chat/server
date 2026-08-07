namespace ArgonComplexTest.Tests;

using Argon.Api.Features.AdminApi;
using Argon.Entities;
using Argon.Features.Admin;
using Argon.Grains.Interfaces;
using ArgonContracts;
using ConsoleContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The admin console is the largest single surface in the server and had no test at all — every
/// operator action (blocking users, granting premium, resolving reports, managing feature flags and
/// tenants) shipped unexercised.
/// <para>
/// The tests drive <see cref="AdminConsoleImpl"/> directly rather than over its Ion port. That port
/// is guarded by <c>OperatorAuthInterceptor</c>, which validates an operator JWT against a live JWKS
/// endpoint — standing up an OIDC provider would test the interceptor, not the console. The
/// interceptor's only output is the ambient <see cref="OperatorRequestContext"/>, so setting that
/// directly reproduces exactly the state every method runs under, including the audit trail.
/// </para>
/// </summary>
[TestFixture]
public class AdminConsoleTests : TestBase
{
    private static readonly Guid OperatorId = Guid.Parse("00000000-0000-0000-0000-0000000ad001");

    /// <summary>
    /// Operator-management calls check that the caller is a *system* operator against the database,
    /// not against the token — so an ambient context alone is not enough and the row has to exist.
    /// </summary>
    [OneTimeSetUp]
    public async Task SeedSystemOperator()
    {
        await using var db = await NewDbAsync(CancellationToken.None);

        if (await db.Operators.AnyAsync(o => o.Id == OperatorId, CancellationToken.None))
            return;

        db.Operators.Add(new OperatorEntity
        {
            Id               = OperatorId,
            DisplayName      = "Integration Test System Operator",
            Email            = "operator@argon.test",
            IsActive         = true,
            IsSystemOperator = true
        });

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Resolves the console with an operator identity in scope. Every admin method audits through
    /// <c>IOperatorAuditService</c>, which quietly no-ops without a context — a test that forgot this
    /// would still pass while silently skipping the audit path.
    /// </summary>
    private (AsyncServiceScope Scope, IAdminConsole Console) Admin()
    {
        var scope = FactoryAsp.Services.CreateAsyncScope();

        OperatorRequestContext.Set(new OperatorRequestContextData
        {
            UserId                = Guid.Parse("00000000-0000-0000-0000-0000000ad000"),
            OperatorId            = OperatorId,
            Email                 = "operator@argon.test",
            CertificateThumbprint = "TEST-THUMBPRINT"
        });

        return (scope, scope.ServiceProvider.GetRequiredService<IAdminConsole>());
    }

    private async Task<Guid> RegisterUserAsync(CancellationToken ct)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);
        return (await GetUserService(scope.ServiceProvider).GetMe(ct)).userId;
    }

    private async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    // ── User search and card ────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task SearchUser_ByIdUsernameAndEmail_AllResolveTheSameUser(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);
        var creds  = FakedTestCreds;

        var (scope, admin) = Admin();
        await using var _ = scope;

        var byId       = await admin.SearchUser(userId.ToString(), ct);
        var byUsername = await admin.SearchUser(creds.username, ct);
        var byEmail    = await admin.SearchUser(creds.email, ct);

        Assert.Multiple(() =>
        {
            Assert.That(byId.found, Is.True);
            Assert.That(byId.userId, Is.EqualTo(userId));
            Assert.That(byId.matchedBy, Is.EqualTo(SearchMatchKind.UserId));

            Assert.That(byUsername.found, Is.True);
            Assert.That(byUsername.userId, Is.EqualTo(userId));
            Assert.That(byUsername.matchedBy, Is.EqualTo(SearchMatchKind.Username));

            Assert.That(byEmail.found, Is.True);
            Assert.That(byEmail.userId, Is.EqualTo(userId));
            Assert.That(byEmail.matchedBy, Is.EqualTo(SearchMatchKind.Email));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task SearchUser_WithBlankOrUnknownQuery_FindsNothing(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var blank   = await admin.SearchUser("   ", ct);
        var unknown = await admin.SearchUser($"nobody_{Guid.NewGuid():N}", ct);

        Assert.Multiple(() =>
        {
            Assert.That(blank.found, Is.False);
            Assert.That(blank.matchedBy, Is.EqualTo(SearchMatchKind.None));
            Assert.That(unknown.found, Is.False);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GetUserCard_ReturnsTheUsersProfileAndRelations(CancellationToken ct = default)
    {
        var userId  = await RegisterUserAsync(ct);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var card = await admin.GetUserCard(userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(card.account.userId, Is.EqualTo(userId));
            Assert.That(card.account.username, Is.EqualTo(FakedTestCreds.username));
            Assert.That(card.spaces.Values.Select(s => s.spaceId), Does.Contain(spaceId));
            Assert.That(card.level, Is.Not.Null, "every user has a level card, defaulted if never awarded");
            Assert.That(card.isBot, Is.False);
        });
    }

    [Test, CancelAfter(120_000)]
    public void GetUserCard_ForAnUnknownUser_Throws()
    {
        var (scope, admin) = Admin();
        using var _ = scope;

        Assert.ThrowsAsync<InvalidOperationException>(() => admin.GetUserCard(Guid.NewGuid()));
    }

    // ── Moderation actions ──────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task BlockThenUnblockUser_RoundTripsTheLockdownState(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var blocked = await admin.BlockUser(userId, LockdownReason.SPAM_SCAM_ACCOUNT, DateTime.UtcNow.AddDays(7), true, ct);
        Assert.That(blocked.success, Is.True, blocked.error);

        await using (var db = await NewDbAsync(ct))
        {
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
            Assert.Multiple(() =>
            {
                Assert.That(user.LockdownReason, Is.EqualTo(LockdownReason.SPAM_SCAM_ACCOUNT));
                Assert.That(user.LockDownIsAppealable, Is.True);
                Assert.That(user.LockDownExpiration, Is.Not.Null);
            });
        }

        var unblocked = await admin.UnblockUser(userId, ct);
        Assert.That(unblocked.success, Is.True, unblocked.error);

        await using (var db = await NewDbAsync(ct))
        {
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
            Assert.That(user.LockdownReason, Is.EqualTo(LockdownReason.NONE));
        }
    }

    [Test, CancelAfter(120_000)]
    public async Task BlockUser_ForAnUnknownUser_Fails(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.BlockUser(Guid.NewGuid(), LockdownReason.SPAM_SCAM_ACCOUNT, null, false, ct);

        Assert.That(result.success, Is.False);
    }

    [Test, CancelAfter(120_000)]
    public async Task ChangeUsername_UpdatesTheUserAndRejectsDuplicates(CancellationToken ct = default)
    {
        var firstUser  = await RegisterUserAsync(ct);
        var firstCreds = FakedTestCreds;
        var secondUser = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var newName = $"renamed{Guid.NewGuid():N}"[..20];
        var renamed = await admin.ChangeUsername(secondUser, newName, ct);
        Assert.That(renamed.success, Is.True, renamed.error);

        // Taking a name that already exists must be refused rather than producing two identical
        // usernames — the login path resolves users by normalised username.
        var duplicate = await admin.ChangeUsername(firstUser, newName, ct);
        Assert.That(duplicate.success, Is.False);

        Assert.That((await admin.SearchUser(newName, ct)).userId, Is.EqualTo(secondUser));
        Assert.That((await admin.SearchUser(firstCreds.username, ct)).userId, Is.EqualTo(firstUser));
    }

    [Test, CancelAfter(120_000)]
    public async Task ChangeEmail_UpdatesTheUser(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var newEmail = $"changed_{Guid.NewGuid():N}@test.local";
        var result   = await admin.ChangeEmail(userId, newEmail, ct);

        Assert.That(result.success, Is.True, result.error);
        Assert.That((await admin.SearchUser(newEmail, ct)).userId, Is.EqualTo(userId));
    }

    [Test, CancelAfter(120_000)]
    public async Task RemoveTwoFactorAndPhone_AreIdempotentWhenNeitherIsSet(CancellationToken ct = default)
    {
        // A support operator clearing 2FA on an account that never had it should get a clean answer,
        // not an exception.
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var twoFactor = await admin.RemoveTwoFactor(userId, ct);
        var phone     = await admin.RemovePhoneNumber(userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(twoFactor.success, Is.True, twoFactor.error);
            Assert.That(phone.success, Is.True, phone.error);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task ChangeAuthModeAndOtpMethod_Persist(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var authMode = await admin.ChangeUserAuthMode(userId, ArgonAuthMode.EmailOtp, ct);
        var otp      = await admin.ChangeUserOtpMethod(userId, OtpMethod.Email, ct);

        Assert.Multiple(() =>
        {
            Assert.That(authMode.success, Is.True, authMode.error);
            Assert.That(otp.success, Is.True, otp.error);
        });

        var card = await admin.GetUserCard(userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(card.account.preferredAuthMode, Is.EqualTo(ArgonAuthMode.EmailOtp));
            Assert.That(card.account.preferredOtpMethod, Is.EqualTo(OtpMethod.Email));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GrantXp_RaisesTheUsersLevelProgress(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.GrantXp(userId, 500, ct);
        Assert.That(result.success, Is.True, result.error);

        // Level progress lives in Orleans grain state and is only mirrored into the DB-backed user
        // card by a later flush, so the grain is the authority right after the grant.
        var level = await GetGrainFactory().GetGrain<IUserLevelGrain>(userId).GetLevelDetailsAsync();
        Assert.That(level.totalXp, Is.GreaterThanOrEqualTo(500));
    }

    // ── Platform statistics and diagnostics ─────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task GetPlatformStats_CountsAtLeastTheUsersThisRunCreated(CancellationToken ct = default)
    {
        await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var stats = await admin.GetPlatformStats(ct);

        Assert.Multiple(() =>
        {
            Assert.That(stats.totalUsers, Is.GreaterThan(0));
            Assert.That(stats.totalSpaces, Is.GreaterThanOrEqualTo(0));
            Assert.That(stats.totalMessages, Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GetDiagnostics_ReportsOnTheRuntimeAndDatabase(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var diagnostics = await admin.GetDiagnostics(ct);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.runtime.processorCount, Is.GreaterThan(0));
            Assert.That(diagnostics.database, Is.Not.Null);
            Assert.That(diagnostics.database!.isHealthy, Is.True, "the test host is talking to a live database");
        });
    }

    // ── Inventory: templates, grants, coupons ───────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task CreateItemTemplate_ThenGrantAndDeleteIt(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var templateId = $"admin_test_{Guid.NewGuid():N}"[..24];
        var created = await admin.CreateItemTemplate(
            new CreateItemTemplateInput(templateId, false, true, false, null, ItemScenarioKind.None, new IonArray<string>([])),
            ct);

        Assert.That(created.success, Is.True, created.error);
        Assert.That(created.itemId, Is.Not.Null);

        var templates = await admin.GetItemTemplates(ct);
        Assert.That(templates.templates.Values.Select(i => i.templateId), Does.Contain(templateId));

        var granted = await admin.GrantItem(userId, created.itemId!.Value, ct);
        Assert.That(granted.success, Is.True, granted.error);

        var card = await admin.GetUserCard(userId, ct);
        var grantedItem = card.items.Values.FirstOrDefault(i => i.templateId == templateId);
        Assert.That(grantedItem, Is.Not.Null, "the granted item should show up on the user card");

        var removed = await admin.DeleteItemFromUserInventory(userId, grantedItem!.itemId, ct);
        Assert.That(removed.success, Is.True, removed.error);

        var deletedTemplate = await admin.DeleteItemTemplate(created.itemId!.Value, ct);
        Assert.That(deletedTemplate.success, Is.True, deletedTemplate.error);
    }

    [Test, CancelAfter(120_000)]
    public async Task CreateItemTemplate_WithAnEmptyId_IsRejected(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.CreateItemTemplate(
            new CreateItemTemplateInput("  ", false, false, false, null, ItemScenarioKind.None, new IonArray<string>([])),
            ct);

        Assert.That(result.success, Is.False);
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteItemTemplate_ForAnUnknownId_Fails(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        Assert.That((await admin.DeleteItemTemplate(Guid.NewGuid(), ct)).success, Is.False);
    }

    [Test, CancelAfter(120_000)]
    public async Task CreateCoupon_AppearsInTheListAndRejectsDuplicateCodes(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var code = $"TEST{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var input = new CreateCouponInput(
            code, "integration test coupon",
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(30), 5, null);

        var created = await admin.CreateCoupon(input, ct);
        Assert.That(created.success, Is.True, created.error);

        var coupons = await admin.GetCoupons(ct);
        Assert.That(coupons.coupons.Values.Select(c => c.code), Does.Contain(code));

        var duplicate = await admin.CreateCoupon(input, ct);
        Assert.That(duplicate.success, Is.False, "coupon codes are the redemption key and must stay unique");
    }

    // ── Premium / subscription administration ───────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task GrantPremium_ThenCancelAndExpireIt(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var granted = await admin.GrantPremium(userId, UltimaPlan.Monthly, 30, ct);
        Assert.That(granted.success, Is.True, granted.error);

        var card = await admin.GetUserCard(userId, ct);
        Assert.That(card.premiumInfo, Is.Not.Null);
        Assert.That(card.premiumInfo!.status, Is.EqualTo(UltimaSubscriptionStatus.Active));

        var cancelled = await admin.CancelUserSubscription(userId, ct);
        Assert.That(cancelled.success, Is.True, cancelled.error);

        var expired = await admin.ExpireUserSubscription(userId, ct);
        Assert.That(expired.success, Is.True, expired.error);

        var afterExpiry = await admin.GetUserCard(userId, ct);
        Assert.That(afterExpiry.premiumInfo!.status, Is.Not.EqualTo(UltimaSubscriptionStatus.Active));
    }

    [Test, CancelAfter(120_000)]
    public async Task GetUserTransactions_ForAUserWithNoPayments_IsEmpty(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var page = await admin.GetUserTransactions(userId, 1, 20, ct);

        Assert.Multiple(() =>
        {
            Assert.That(page.transactions.Size, Is.EqualTo(0));
            Assert.That(page.totalCount, Is.EqualTo(0));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GetTransactionByXsollaId_ForAnUnknownId_IsNull(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        Assert.That(await admin.GetTransactionByXsollaId($"missing_{Guid.NewGuid():N}", ct), Is.Null);
    }

    // ── Spaces ──────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task SearchSpace_And_GetSpaceCard_And_GetSpaceMembers(CancellationToken ct = default)
    {
        var userId  = await RegisterUserAsync(ct);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var search = await admin.SearchSpace(spaceId.ToString(), ct);
        Assert.That(search.found, Is.True);

        var card = await admin.GetSpaceCard(spaceId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(card.spaceId, Is.EqualTo(spaceId));
            Assert.That(card.creator.userId, Is.EqualTo(userId));
            Assert.That(card.memberCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(card.archetypes.Size, Is.GreaterThan(0), "a new space is seeded with default archetypes");
        });

        var members = await admin.GetSpaceMembers(spaceId, 0, 50, ct);
        Assert.That(members.members.Values.Select(m => m.userId), Does.Contain(userId));
    }

    [Test, CancelAfter(120_000)]
    public void GetSpaceCard_ForAnUnknownSpace_Throws()
    {
        var (scope, admin) = Admin();
        using var _ = scope;

        Assert.ThrowsAsync<InvalidOperationException>(() => admin.GetSpaceCard(Guid.NewGuid()));
    }

    [Test, CancelAfter(120_000)]
    public async Task SearchSpace_ForAnUnknownSpace_FindsNothing(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        Assert.That((await admin.SearchSpace(Guid.NewGuid().ToString(), ct)).found, Is.False);
    }

    // ── Feature flags ───────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task FeatureFlag_FullLifecycle(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var flagId = $"admin.test.{Guid.NewGuid():N}"[..24];

        var created = await admin.CreateFeatureFlag(
            new CreateFeatureFlagInput(flagId, "created by the admin console tests", false, null, null, null, null), ct);
        Assert.That(created.success, Is.True, created.error);

        Assert.That((await admin.GetFeatureFlags(ct)).flags.Values.Select(f => f.id), Does.Contain(flagId));

        var updated = await admin.UpdateFeatureFlag(
            new UpdateFeatureFlagInput(flagId, "updated", true, 50, null, null, null), ct);
        Assert.That(updated.success, Is.True, updated.error);

        var details = await admin.GetFeatureFlag(flagId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(details.defaultEnabled, Is.True);
            Assert.That(details.rolloutPercentage, Is.EqualTo(50));
        });

        var overrideSet = await admin.SetFeatureFlagOverride(
            new SetFeatureFlagOverrideInput(flagId, 0, userId.ToString(), false, null, null), ct);
        Assert.That(overrideSet.success, Is.True, overrideSet.error);

        var withOverride = await admin.GetFeatureFlag(flagId, ct);
        var theOverride  = withOverride.overrides.Values.FirstOrDefault(o => o.targetId == userId.ToString());
        Assert.That(theOverride, Is.Not.Null);

        Assert.That((await admin.DeleteFeatureFlagOverride(theOverride!.overrideId, ct)).success, Is.True);
        Assert.That((await admin.DeleteFeatureFlag(flagId, ct)).success, Is.True);
        Assert.ThrowsAsync<InvalidOperationException>(() => admin.GetFeatureFlag(flagId));
    }

    [Test, CancelAfter(120_000)]
    public void GetFeatureFlag_ForAnUnknownFlag_Throws()
    {
        var (scope, admin) = Admin();
        using var _ = scope;

        Assert.ThrowsAsync<InvalidOperationException>(() => admin.GetFeatureFlag($"nope.{Guid.NewGuid():N}"));
    }

    // ── Tenant directory ────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Tenant_FullLifecycle(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var domain = $"t{Guid.NewGuid():N}.test.local";

        var created = await admin.CreateTenant(
            new CreateTenantInput(domain, $"https://{domain}", "Test Org", null, "created by tests"), ct);
        Assert.That(created.success, Is.True, created.error);
        Assert.That(created.tenantId, Is.Not.Null);

        var tenantId = created.tenantId!.Value;

        Assert.That((await admin.GetTenantDirectory(ct)).tenants.Values.Select(t => t.domain), Does.Contain(domain));

        var updated = await admin.UpdateTenant(
            new UpdateTenantInput(tenantId, $"https://updated.{domain}", "Updated Org", "updated"), ct);
        Assert.That(updated.success, Is.True, updated.error);

        var verified = await admin.SetTenantVerified(tenantId, true, ct);
        Assert.That(verified.success, Is.True, verified.error);

        var afterVerify = (await admin.GetTenantDirectory(ct)).tenants.Values.First(t => t.tenantId == tenantId);
        Assert.Multiple(() =>
        {
            Assert.That(afterVerify.isVerified, Is.True);
            Assert.That(afterVerify.instanceUrl, Does.Contain("updated"));
        });

        Assert.That((await admin.DeleteTenant(tenantId, ct)).success, Is.True);
        Assert.That((await admin.GetTenantDirectory(ct)).tenants.Values.Select(t => t.tenantId), Does.Not.Contain(tenantId));
    }

    [Test, CancelAfter(120_000)]
    public async Task CreateTenant_WithADuplicateDomain_IsRejected(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var domain = $"d{Guid.NewGuid():N}.dup.local";
        var input  = new CreateTenantInput(domain, $"https://{domain}", null, null, null);

        Assert.That((await admin.CreateTenant(input, ct)).success, Is.True);
        Assert.That((await admin.CreateTenant(input, ct)).success, Is.False,
            "the directory resolves instances by domain, so domains must stay unique");
    }

    // ── Operators and the audit log ─────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Operator_CreateDeactivateActivate(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var email   = $"op_{Guid.NewGuid():N}@argon.test";
        var created = await admin.CreateOperator(new CreateOperatorInput("Test Operator", email, null, false), ct);

        Assert.That(created.success, Is.True, created.error);
        var newOperatorId = created.operatorId!.Value;

        Assert.That((await admin.GetOperators(ct)).operators.Values.Select(o => o.operatorId), Does.Contain(newOperatorId));

        var details = await admin.GetOperatorDetails(newOperatorId, ct);
        Assert.That(details.info.email, Is.EqualTo(email));

        Assert.That((await admin.DeactivateOperator(newOperatorId, ct)).success, Is.True);
        Assert.That((await admin.GetOperatorDetails(newOperatorId, ct)).info.isActive, Is.False);

        Assert.That((await admin.ActivateOperator(newOperatorId, ct)).success, Is.True);
        Assert.That((await admin.GetOperatorDetails(newOperatorId, ct)).info.isActive, Is.True);
    }

    [Test, CancelAfter(120_000)]
    public async Task CreateOperator_WithADuplicateEmail_IsRejected(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var email = $"dup_{Guid.NewGuid():N}@argon.test";
        var input = new CreateOperatorInput("Duplicate", email, null, false);

        Assert.That((await admin.CreateOperator(input, ct)).success, Is.True);
        Assert.That((await admin.CreateOperator(input, ct)).success, Is.False);
    }

    [Test, CancelAfter(120_000)]
    public void GetOperatorDetails_ForAnUnknownOperator_Throws()
    {
        var (scope, admin) = Admin();
        using var _ = scope;

        Assert.ThrowsAsync<InvalidOperationException>(() => admin.GetOperatorDetails(Guid.NewGuid()));
    }

    [Test, CancelAfter(120_000)]
    public async Task GetOperatorAppAccess_ForAFreshOperator_IsEmpty(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var created = await admin.CreateOperator(
            new CreateOperatorInput("Access Test", $"acc_{Guid.NewGuid():N}@argon.test", null, false), ct);

        var access = await admin.GetOperatorAppAccess(created.operatorId!.Value, ct);

        Assert.That(access.entries.Size, Is.EqualTo(0));
    }

    [Test, CancelAfter(120_000)]
    public async Task GetAuditLog_RecordsTheActionsThisFixturePerformed(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        await admin.GrantXp(userId, 10, ct);

        var page = await admin.GetAuditLog(new AuditLogQuery(OperatorId, null, null, null, null, 1, 50), ct);

        Assert.Multiple(() =>
        {
            Assert.That(page.totalCount, Is.GreaterThan(0), "admin actions must leave an audit trail");
            Assert.That(page.entries.Values.All(e => e.operatorId == OperatorId), Is.True);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GetAuditLog_FilteredByAnUnusedAction_IsEmpty(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var page = await admin.GetAuditLog(
            new AuditLogQuery(null, $"never_happened_{Guid.NewGuid():N}", null, null, null, 1, 20), ct);

        Assert.That(page.totalCount, Is.EqualTo(0));
    }

    // ── Bots and teams ──────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task SearchBot_ForAnUnknownQuery_FindsNothing(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        Assert.That((await admin.SearchBot($"nobot_{Guid.NewGuid():N}", ct)).found, Is.False);
    }

    [Test, CancelAfter(120_000)]
    public void GetBotCard_ForAnUnknownApp_Throws()
    {
        var (scope, admin) = Admin();
        using var _ = scope;

        Assert.ThrowsAsync<InvalidOperationException>(() => admin.GetBotCard(Guid.NewGuid()));
    }

    [Test, CancelAfter(120_000)]
    public async Task SearchTeam_ForAnUnknownQuery_FindsNothing(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        Assert.That((await admin.SearchTeam($"noteam_{Guid.NewGuid():N}", ct)).found, Is.False);
    }

    [Test, CancelAfter(120_000)]
    public void GetTeamCard_ForAnUnknownTeam_Throws()
    {
        var (scope, admin) = Admin();
        using var _ = scope;

        Assert.ThrowsAsync<InvalidOperationException>(() => admin.GetTeamCard(Guid.NewGuid()));
    }

    [Test, CancelAfter(120_000)]
    public async Task SearchInternalApps_ReturnsAResult(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var result = await admin.SearchInternalApps("argon", ct);

        Assert.That(result.apps.Size, Is.GreaterThanOrEqualTo(0));
    }

    // ── Reports and trust ───────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task GetReports_WithNoFilters_ReturnsAPage(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var page = await admin.GetReports(null, null, 20, 0, ct);

        Assert.That(page.totalCount, Is.GreaterThanOrEqualTo(0));
    }

    [Test, CancelAfter(120_000)]
    public async Task GetReports_FilteredByStatus_ReturnsOnlyThatStatus(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var page = await admin.GetReports(ReportStatus.PENDING, null, 20, 0, ct);

        Assert.That(page.reports.Values.All(r => r.status == ReportStatus.PENDING), Is.True);
    }

    [Test, CancelAfter(120_000)]
    public void GetReportById_ForAnUnknownReport_Throws()
    {
        var (scope, admin) = Admin();
        using var _ = scope;

        Assert.ThrowsAsync<KeyNotFoundException>(() => admin.GetReportById(Guid.NewGuid()));
    }

    [Test, CancelAfter(120_000)]
    public async Task GetUserTrustCard_ForANewUser_ReturnsTheDefaultScore(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var card = await admin.GetUserTrustCard(userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(card.userId, Is.EqualTo(userId));
            Assert.That(card.trustScore, Is.InRange(0, 100));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RecalculateUserTrust_ForANewUser_Succeeds(CancellationToken ct = default)
    {
        var userId = await RegisterUserAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var card = await admin.RecalculateUserTrust(userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(card.userId, Is.EqualTo(userId));
            Assert.That(card.trustScore, Is.InRange(0, 100));
        });
    }
}
