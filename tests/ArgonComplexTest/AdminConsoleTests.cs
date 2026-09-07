namespace ArgonComplexTest.Tests;

using Argon.Api.Features.AdminApi;
using Argon.Entities;
using Argon.Features.Admin;
using Argon.Features.Testing;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using ArgonComplexTest.Infrastructure.Presence;
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

    // ── The inactivity deletion queue ───────────────────────────────────────────────────────────
    //
    // The daily sweep no longer deletes dormant accounts: it proposes them, and an operator decides
    // here. That makes this console the whole of the safety property the rework was for, so the tests
    // below are about the decision rather than about the mapping — that a proposal is visible with the
    // arithmetic behind it, that approving takes the guarded path and leaves a trail, and that
    // rejecting is remembered by the next sweep rather than silently re-proposed.
    //
    // The queue is one grain shared by the whole run, so every assertion names an account; none of
    // them counts entries.

    /// <summary>Seeds a dormant account and runs one sweep, so there is something in the queue.</summary>
    private async Task<TestUserSession> ProposeDormantAccountAsync(CancellationToken ct)
    {
        var dormant = await CreateSessionAsync(ct);

        await AccountSeed.BackdateLastLoginAsync(dormant.UserId, DateTimeOffset.UtcNow - TimeSpan.FromDays(400), ct: ct);

        await RunSweepAsync();

        return dormant;
    }

    private Task RunSweepAsync()
        => GetGrainFactory()
          .GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId)
          .RunScanAsync()
          .AsTask();

    /// <summary>The console's view of one account's queue entry, paging until it is found.</summary>
    /// <remarks>
    /// The whole suite shares one queue — every fixture's dormant accounts land in it — so paging rather
    /// than reading the first page is what keeps a "not queued" answer honest.
    /// </remarks>
    private static async Task<AccountDeletionQueueEntry?> QueuedAsync(IAdminConsole admin, Guid userId, CancellationToken ct)
    {
        const int size = 200;

        for (var offset = 0; ; offset += size)
        {
            var page = await admin.GetAccountDeletionQueue(offset, size, ct);

            if (page.entries.Values.FirstOrDefault(entry => entry.userId == userId) is { } found)
                return found;

            if (page.entries.Values.Count < size || offset + page.entries.Values.Count >= page.totalCount)
                return null;
        }
    }

    /// <summary>
    /// The queue shows an operator who is being proposed, and on what evidence.
    /// </summary>
    /// <remarks>
    /// An operator approving one of these is erasing somebody's account, so the entry has to carry enough
    /// to tell a real candidate from a mistake without leaving the page: who it is, how long they have
    /// been silent, and which threshold that was measured against — the platform's or a shorter one the
    /// account chose for itself. The identity fields are joined on the way out rather than copied into
    /// grain state, so this also pins that the join happens at all.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task GetAccountDeletionQueue_ListsTheDormantAccountsTheSweepProposed(CancellationToken ct = default)
    {
        var dormant = await ProposeDormantAccountAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var entry = await QueuedAsync(admin, dormant.UserId, ct);

        Assert.That(entry, Is.Not.Null, "the sweep proposed an account and the console cannot see it");

        Assert.Multiple(() =>
        {
            Assert.That(entry!.username, Is.EqualTo(dormant.Credentials.username),
                "an operator decides on a person, not on a guid");
            Assert.That(entry.email, Is.EqualTo(dormant.Credentials.email));
            Assert.That(entry.thresholdMonths, Is.EqualTo(12), "the platform default this account never moved off");
            Assert.That(entry.reason, Is.EqualTo(AccountDeletionQueueReasons.InactivityDefault));
            Assert.That(entry.lastActivityAt, Is.LessThan(DateTimeOffset.UtcNow - TimeSpan.FromDays(365)),
                "the entry has to show the silence it is claiming");
            Assert.That(entry.state, Is.EqualTo(AccountDeletionQueueEntryState.PENDING));
            Assert.That(entry.decidedByOperatorId, Is.Null);
            Assert.That(entry.scheduledDeletionAt, Is.Null,
                "a proposal is not a deletion; nothing is scheduled until somebody says so");
        });
    }

    /// <summary>
    /// Approving schedules the deletion through the guarded path, tells the account, and leaves a trail.
    /// </summary>
    /// <remarks>
    /// <para>Four things have to be true at once for an approval to be a decision rather than a button:
    /// the deletion is actually scheduled, the account holder is told in time to object (the notice mail,
    /// which is now sent at approval rather than by the sweep), the entry records who approved it, and
    /// the operator audit log records the same thing durably — the queue entry is retired by the next
    /// sweep, the audit row is what survives.</para>
    ///
    /// <para>The grace period the account then has is the ordinary one, so the account holder can still
    /// cancel from their own console; that half belongs to <c>AccountConsoleTests</c>.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task ApproveAccountDeletion_SchedulesTheDeletionAndRecordsWhoApprovedIt(CancellationToken ct = default)
    {
        var dormant = await ProposeDormantAccountAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var approved = await admin.ApproveAccountDeletion(dormant.UserId, ct);

        Assert.That(approved.success, Is.True, approved.error);

        var status = await GetGrainFactory().GetGrain<IAccountDeletionGrain>(dormant.UserId).GetDeletionStatusAsync();
        var notice = await AccountTimings.Emails.WaitForAsync(
            dormant.Credentials.email, EmailKinds.DeleteNotice, AccountTimings.Slack, ct);

        var entry = await QueuedAsync(admin, dormant.UserId, ct);

        // Page zero. OperatorAuditService pages with Skip(page * pageSize), so asking for page one of a
        // two-row result returns nothing at all — which is how the assertions below could once be
        // written against an empty list and pass.
        var audit = await admin.GetAuditLog(
            new AuditLogQuery(OperatorId, "ApproveAccountDeletion", dormant.UserId.ToString(), null, null, 0, 20), ct);

        Assert.Multiple(() =>
        {
            Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.Scheduled),
                "an approval has to schedule the deletion it approved");
            Assert.That(status.ExecutionAt, Is.Not.Null, "and the account holder needs the date to object by");

            Assert.That(notice, Is.Not.Null,
                "the only warning before an unrequested erasure, and it is the approval that owes it");

            Assert.That(entry, Is.Not.Null,
                "an approval has to stay on the worklist while the deletion it authorised is still running: "
              + "approving is what takes the account out of the sweep's candidate set, so an entry retired on "
              + "'no longer a candidate' alone would vanish a day after the decision and inside the grace");
            Assert.That(entry!.state, Is.EqualTo(AccountDeletionQueueEntryState.APPROVED));
            Assert.That(entry.decidedByOperatorId, Is.EqualTo(OperatorId));
            Assert.That(entry.decidedAt, Is.Not.Null);
            Assert.That(entry.scheduledDeletionAt, Is.EqualTo(status.ExecutionAt));

            Assert.That(audit.totalCount, Is.GreaterThan(0),
                "an irreversible operator action with no audit row is an action nobody can answer for");
            Assert.That(audit.entries.Values.All(row => row.operatorId == OperatorId), Is.True);

            // Both halves of the record, and the order they are written in is the point. The audit
            // line used to be written after the deletion was scheduled and the account mailed, only on
            // the success path, and inside the try whose catch reports a failure — so an audit store
            // that was briefly unavailable erased the only record of who ordered an erasure that was
            // going to happen anyway.
            Assert.That(audit.entries.Values.Any(row => row.details?.Contains("Outcome=attempted") ?? false), Is.True,
                "nothing recorded that this operator was about to schedule an erasure; the record has "
              + "to be written before the irreversible act, not after it");
            Assert.That(audit.entries.Values.Any(row => row.details?.Contains("Outcome=scheduled") ?? false), Is.True,
                "the outcome of the approval was not recorded");
        });
    }

    /// <summary>
    /// Rejecting drops the entry, records the refusal, and the next sweep does not re-propose the account.
    /// </summary>
    /// <remarks>
    /// The hold is the whole test. Entries are a projection of the last sweep, so a rejection that only
    /// removed one would last until the next pass and the same dormant account would be back tomorrow —
    /// which is defect CON-4 (a refusal the system cannot remember) with an operator standing in for the
    /// account holder. The second sweep is what proves the refusal outlived the entry.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task RejectAccountDeletion_DropsTheEntryAndKeepsTheSweepAway(CancellationToken ct = default)
    {
        var dormant = await ProposeDormantAccountAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        Assert.That(await QueuedAsync(admin, dormant.UserId, ct), Is.Not.Null,
            "the account has to be queued for a rejection to mean anything");

        var rejected = await admin.RejectAccountDeletion(dormant.UserId, ct);

        Assert.That(rejected.success, Is.True, rejected.error);

        var afterRejection = await QueuedAsync(admin, dormant.UserId, ct);

        await RunSweepAsync();

        var afterSweep = await QueuedAsync(admin, dormant.UserId, ct);
        var status     = await GetGrainFactory().GetGrain<IAccountDeletionGrain>(dormant.UserId).GetDeletionStatusAsync();
        var audit      = await admin.GetAuditLog(
            new AuditLogQuery(OperatorId, "RejectAccountDeletion", dormant.UserId.ToString(), null, null, 1, 20), ct);

        Assert.Multiple(() =>
        {
            Assert.That(afterRejection, Is.Null, "a rejected account is off the worklist immediately");
            Assert.That(afterSweep, Is.Null,
                "the very next sweep proposed the same account again; the refusal has to outlive the entry");
            Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.None),
                "and nothing was scheduled behind the operator's back");
            Assert.That(audit.totalCount, Is.GreaterThan(0), "a refusal is a decision and is audited like one");
            Assert.That(AccountTimings.Emails.Sent(dormant.Credentials.email, EmailKinds.DeleteNotice),
                Is.Empty, "an account nobody approved must never be told it is being deleted");
        });
    }

    /// <summary>
    /// An account nobody proposed cannot be approved from the console.
    /// </summary>
    /// <remarks>
    /// The queue is the only way into this action, and that is deliberate: approving is the one operator
    /// action here that ends in an irreversible erasure, and it is available exactly for the accounts the
    /// sweep's arithmetic selected. Without the check it would be a delete-any-account button reachable
    /// with a guid.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task ApproveAccountDeletion_ForAnAccountNobodyProposed_IsRefused(CancellationToken ct = default)
    {
        var bystander = await CreateSessionAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var approved = await admin.ApproveAccountDeletion(bystander.UserId, ct);
        var status   = await GetGrainFactory().GetGrain<IAccountDeletionGrain>(bystander.UserId).GetDeletionStatusAsync();
        var audit    = await admin.GetAuditLog(
            new AuditLogQuery(OperatorId, "ApproveAccountDeletion", bystander.UserId.ToString(), null, null, 0, 20), ct);

        Assert.Multiple(() =>
        {
            Assert.That(approved.success, Is.False,
                "the console scheduled the deletion of an account the sweep never proposed");
            Assert.That(approved.error, Is.Not.Null.And.Not.Empty, "and the operator has to be told why");
            Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.None));

            // A refused approval is operator behaviour a review wants to see — somebody repeatedly
            // trying to erase an account the guards keep refusing is exactly the pattern an audit log
            // exists for — and it used to be logged to the application log and nowhere else.
            Assert.That(audit.entries.Values.Any(row => row.details?.Contains("Outcome=refused") ?? false), Is.True,
                "an attempt to erase an account that the console refused left no audit trail at all");
        });
    }

    /// <summary>
    /// An approval whose record was lost adopts the deletion it armed instead of refusing for ever.
    /// </summary>
    /// <remarks>
    /// <para>The failure this pins: <c>ApproveAsync</c> used to schedule the deletion first and write
    /// down who approved it second. A queue write lost between the two — a storage blip, an activation
    /// that went away — left a <c>Pending</c> entry in front of an armed erasure. Approving again
    /// answered <c>AlreadyScheduled</c>, so the entry could never be approved; rejecting it dropped the
    /// row and recorded a decline while the countdown kept running, and thirty days later the account
    /// was erased with nothing anywhere saying on whose say-so.</para>
    ///
    /// <para>The lost write is produced by arming the deletion the same way an approval arms it —
    /// <c>RequestAutoDeleteAsync</c>, the guarded path — and leaving the queue ignorant of it, which is
    /// exactly the state the interrupted approval leaves behind.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task ApproveAccountDeletion_adopts_a_deletion_that_is_already_armed(CancellationToken ct = default)
    {
        var dormant = await ProposeDormantAccountAsync(ct);
        var grain   = GetGrainFactory().GetGrain<IAccountDeletionGrain>(dormant.UserId);

        var armed = await grain.RequestAutoDeleteAsync();

        Assert.That(armed.Success, Is.True, armed.Error?.ToString());

        var (scope, admin) = Admin();
        await using var _ = scope;

        var approved = await admin.ApproveAccountDeletion(dormant.UserId, ct);
        var status   = await grain.GetDeletionStatusAsync();
        var entry    = await QueuedAsync(admin, dormant.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(approved.success, Is.True,
                $"the approval was refused over an erasure that is already armed: {approved.error}");

            Assert.That(entry, Is.Not.Null, "the entry disappeared instead of recording the decision");
            Assert.That(entry!.state, Is.EqualTo(AccountDeletionQueueEntryState.APPROVED),
                "the entry is still Pending in front of a running deletion, so it can be neither "
              + "approved nor rejected and the erasure has nobody's name against it");
            Assert.That(entry.decidedByOperatorId, Is.EqualTo(OperatorId));
            Assert.That(entry.scheduledDeletionAt, Is.EqualTo(status.ExecutionAt),
                "the adopted schedule is not the one the deletion is actually running to");
        });
    }

    /// <summary>
    /// A rejection cannot quietly drop an entry whose deletion is already running.
    /// </summary>
    /// <remarks>
    /// The other half of the lost-write case above. <c>RejectAsync</c> read the entry's own state to
    /// decide whether a countdown was running — and the entry is the queue's belief, while the deletion
    /// grain holds the fact. When they disagreed, rejecting removed the row and recorded a decline hold
    /// while the erasure carried on to completion. The account holder can still call it off from their
    /// own console; an operator cannot, and the refusal says so.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task RejectAccountDeletion_will_not_drop_an_entry_whose_deletion_is_running(CancellationToken ct = default)
    {
        var dormant = await ProposeDormantAccountAsync(ct);
        var grain   = GetGrainFactory().GetGrain<IAccountDeletionGrain>(dormant.UserId);

        var armed = await grain.RequestAutoDeleteAsync();

        Assert.That(armed.Success, Is.True, armed.Error?.ToString());

        var (scope, admin) = Admin();
        await using var _ = scope;

        var rejected = await admin.RejectAccountDeletion(dormant.UserId, ct);
        var status   = await grain.GetDeletionStatusAsync();
        var entry    = await QueuedAsync(admin, dormant.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(rejected.success, Is.False,
                "an operator called off a running erasure through the queue, which is the account "
              + "holder's decision to make");
            Assert.That(rejected.error, Does.Contain("already scheduled"),
                "and the refusal has to say why, since the operator can see no countdown from here");

            Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.Scheduled),
                "the deletion was left running by a rejection that reported success");
            Assert.That(entry, Is.Not.Null,
                "the entry was dropped, so the only remaining trace of the armed erasure is gone too");
        });

        // Left tidy: the account holder's own cancellation is what ends this, and the fixture should not
        // leave an erasure running against an account it seeded for one assertion.
        await grain.CancelDeletionAsync();
    }

    /// <summary>
    /// A decided entry is settled by its deletion, never by the account turning up as a candidate again.
    /// </summary>
    /// <remarks>
    /// <para>Defect R23. The reconciliation kept any entry whose account the scan proposed, before it
    /// looked at what the entry was. An approval the account holder then cancelled therefore came back
    /// to life a year later, when the decline hold lapsed and the dormant account was proposed again:
    /// the year-old <c>Approved</c> record was carried forward instead of being replaced with a fresh
    /// proposal, and both decisions refused it — approve answered "already approved", reject answered
    /// "already scheduled, only the holder can cancel". Nothing short of editing grain state could
    /// clear it.</para>
    ///
    /// <para>The re-proposal is made through the queue's own contract rather than by waiting out the
    /// decline hold, which is a year at shipped values and is the scheduler's rule rather than the
    /// queue's. The candidate set is rebuilt from what is already pending so that this fixture's own
    /// worklist is not retired out from under the tests around it.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_decided_entry_that_is_proposed_again_becomes_a_fresh_proposal(CancellationToken ct = default)
    {
        var dormant = await ProposeDormantAccountAsync(ct);
        var grain   = GetGrainFactory().GetGrain<IAccountDeletionGrain>(dormant.UserId);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var approved = await admin.ApproveAccountDeletion(dormant.UserId, ct);

        Assert.That(approved.success, Is.True, approved.error);

        // The account holder changes their mind inside the grace period.
        var cancelled = await grain.CancelDeletionAsync();

        Assert.That(cancelled.Success, Is.True, cancelled.Error?.ToString());

        await ReproposeAsync(admin, dormant.UserId, ct);

        var entry = await QueuedAsync(admin, dormant.UserId, ct);

        Assert.That(entry, Is.Not.Null,
            "the account was proposed again and the queue has no entry for it at all");

        Assert.Multiple(() =>
        {
            Assert.That(entry!.state, Is.EqualTo(AccountDeletionQueueEntryState.PENDING),
                "the stale approval was carried forward instead of being replaced by a fresh proposal, "
              + "so the row shows an approved deletion that is not scheduled and never will be");
            Assert.That(entry.decidedByOperatorId, Is.Null,
                "a fresh proposal still carries the old decision");
            Assert.That(entry.scheduledDeletionAt, Is.Null);
        });

        // And it is workable again, which is the whole point of the row being Pending.
        var again = await admin.ApproveAccountDeletion(dormant.UserId, ct);

        Assert.That(again.success, Is.False,
            "the account holder's refusal was not honoured on the second approval");
        Assert.That(again.error, Does.Contain("RecentlyDeclined"),
            $"the second approval was refused for the wrong reason: {again.error}");
    }

    /// <summary>
    /// An erasure that gives up half way is listed for an operator, and an operator can finish it.
    /// </summary>
    /// <remarks>
    /// <para>Defect R5, from the console's side. A deletion that spends its execution attempts
    /// unregisters its own poll and is never armed again, and every surface that could have shown that
    /// erased it: the account holder cannot sign in to ask (the erasure took their password digest and
    /// address on its third step), the scan will never propose an anonymised row again, and the queue
    /// retired its entry as soon as the deletion stopped running. What was left of a half-erased
    /// account — memberships still live, devices and passkeys still held, file references half released
    /// — was a metric and a log line.</para>
    ///
    /// <para>The failure is made deterministic the way <c>AccountDeletionTests</c> makes it:
    /// <c>NormalizedEmail</c> is a computed column with a unique index, so parking
    /// <c>deleted_{userId}@void.local</c> on another row makes the anonymising step violate it, every
    /// time, before its own write commits. Clearing the collision is what lets the resumed erasure
    /// finish for real, which is what proves the resume is a resume and not a status flag.</para>
    ///
    /// <para><b>And the worklist says so in its own state column</b> (defect F9). The wire enum had
    /// two members and the mapping was <c>Pending ? PENDING : APPROVED</c>, so a stranded erasure was
    /// rendered as an approval that was progressing normally — <c>ListAsync</c> sorts it into the
    /// middle band precisely because it is work owed, and the console then flattened that away. The
    /// only remaining signal was a non-null <c>strandedSince</c>, which a screen rendering a state
    /// badge does not look at, so an operator scanning their own approvals had no reason to open the
    /// stranded page at all. <c>AccountDeletionQueueEntryState.STRANDED</c> is what the row carries
    /// now.</para>
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task A_stranded_erasure_is_listed_for_an_operator_and_can_be_resumed(CancellationToken ct = default)
    {
        var dormant = await ProposeDormantAccountAsync(ct);
        var decoy   = await CreateSessionAsync(ct);
        var grain   = GetGrainFactory().GetGrain<IAccountDeletionGrain>(dormant.UserId);

        var collision = $"deleted_{dormant.UserId}@void.local";

        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Users.Where(u => u.Id == decoy.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.Email, collision), ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var approved = await admin.ApproveAccountDeletion(dormant.UserId, ct);

        Assert.That(approved.success, Is.True, approved.error);

        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var gaveUp = await Poll.ForValueAsync(
            async () =>
            {
                await grain.CheckAndExecuteAsync();
                return await grain.GetDeletionStatusAsync();
            },
            reported => reported.Stranded,
            AccountTimings.ExecutionBudget, AccountTimings.Slack / 4, ct);

        Assert.That(gaveUp.Stranded, Is.True,
            $"the approved erasure never gave up, so there is nothing to surface; it is {gaveUp.Status} "
          + $"after {gaveUp.ExecutionAttempts} attempt(s) with reason '{gaveUp.FailureReason}'");

        // The queue learns about it on the pass it would learn anything on.
        await RunSweepAsync();

        var stranded = await StrandedAsync(admin, dormant.UserId, ct);
        var entry    = await QueuedAsync(admin, dormant.UserId, ct);

        Assert.That(stranded, Is.Not.Null,
            "an erasure gave up half way through somebody's account and no operator surface lists it");

        Assert.Multiple(() =>
        {
            Assert.That(stranded!.failureReason, Is.Not.Null.And.Not.Empty,
                "an operator has to be told what stopped it before deciding to run it again");
            Assert.That(stranded.executionAttempts, Is.EqualTo(AccountTimings.Deletion.MaxExecutionAttempts),
                "the attempt count an operator reads is not the one the deletion actually spent");
            Assert.That(stranded.approvedByOperatorId, Is.EqualTo(OperatorId),
                "the record of who authorised the erasure did not survive its failure");
            Assert.That(stranded.strandedSince, Is.Not.Null);

            Assert.That(entry, Is.Not.Null,
                "the queue retired the entry of an approval whose erasure broke, which is the state it "
              + "most needs to keep");
            Assert.That(entry!.strandedSince, Is.Not.Null,
                "the worklist gives no sign that this approval ended half way");
            Assert.That(entry.state, Is.EqualTo(AccountDeletionQueueEntryState.STRANDED),
                "the queue page labels a stranded erasure as an ordinary approval, so an operator "
              + "reading the state column sees an approval in progress and never opens the page that "
              + "is asking them to finish it");
        });

        // Clear the cause and let an operator finish it.
        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Users.Where(u => u.Id == decoy.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.Email, decoy.Credentials.email), ct);

        var resumed = await admin.ResumeAccountDeletion(dormant.UserId, ct);

        Assert.That(resumed.success, Is.True, resumed.error);

        var finished = await AccountConsoleHarness.DriveDeletionUntilAsync(
            dormant.UserId, AccountDeletionStatusKind.Completed, AccountTimings.ExecutionBudget, ct);

        var audit = await admin.GetAuditLog(
            new AuditLogQuery(OperatorId, "ResumeAccountDeletion", dormant.UserId.ToString(), null, null, 0, 20), ct);

        var stillStranded = await StrandedAsync(admin, dormant.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(finished, Is.EqualTo(AccountDeletionStatusKind.Completed),
                $"the resumed erasure did not finish; it is {finished}");

            Assert.That(stillStranded, Is.Null,
                "a resumed erasure is still on the stranded page, so the next operator resumes it again");

            Assert.That(audit.entries.Values.Any(row => row.details?.Contains("Outcome=attempted") ?? false), Is.True,
                "finishing an erasure by hand is an irreversible operator action and has to be recorded "
              + "before it happens");
            Assert.That(audit.entries.Values.Any(row => row.details?.Contains("Outcome=resumed") ?? false), Is.True,
                "and its outcome recorded after it");
        });
    }

    /// <summary>
    /// The queue keeps showing an approval once its erasure has anonymised the account, for as long as
    /// the retention window says and no longer.
    /// </summary>
    /// <remarks>
    /// <para>Defect R21. The page joined the grain's entries against <c>Users</c> under the global
    /// <c>!IsDeleted</c> filter and dropped any entry whose account did not come back — and setting
    /// <c>IsDeleted</c> is step three of the erasure. So an approved account vanished from the console
    /// the moment the deletion it authorised started doing the irreversible half of its work, which is
    /// the window the entry exists for: nine rows over a total of ten, paging off by the number of
    /// erasures in flight, and an operator looking for the outcome of their own approval finding
    /// nothing.</para>
    ///
    /// <para>What the row shows afterwards is the tombstone the erasure wrote — "Deleted Account" and a
    /// <c>deleted_…</c> username — and that is the intended reading: the entry is the record of a
    /// decision, and the account it decided about is gone by design.</para>
    ///
    /// <para><b>And the second half, which is why this test used to be red only under four shards.</b>
    /// Fixing the join left the row's lifetime a race. The queue is one cluster-wide singleton and every
    /// fixture that drives <c>IAutoDeleteSchedulerGrain.RunScanAsync</c> reconciles it, and
    /// reconciliation used to retire a decided entry the instant its deletion reported
    /// <c>Completed</c> — deliberately, "the worklist is for work". So what this test could observe was
    /// bounded by whenever a neighbouring fixture's scan happened to land: alone it passed, and under
    /// four concurrent shard processes a scan landed 0.28 s before the page was read (<c>0 enqueued,
    /// 3 retired</c>) and it failed. Nothing about the product was open — which is exactly what made it
    /// worth fixing in the product rather than in the test, because a promise an operator cannot rely on
    /// for a whole second is not a promise. <c>AccountDeletionOptions.DecisionRetention</c> is that
    /// window (a week shipped, thirty seconds here), the entry becomes <c>COMPLETED</c> rather than
    /// staying <c>APPROVED</c> so the badge does not read as an erasure still inside its grace, and no
    /// reconciliation from anywhere can retire it early. The wait below is what pins the far side: the
    /// row does leave, and it leaves because the window ran out rather than because somebody swept.</para>
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task The_queue_still_shows_an_approval_once_its_erasure_has_run(CancellationToken ct = default)
    {
        var dormant = await ProposeDormantAccountAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        // Taken before the approval rather than before the erasure, because the stamp under test is
        // written by whichever reconciliation first sees the deletion finished — and that is very often
        // a neighbouring fixture's sweep landing the moment the grace elapses, a second or two before
        // this test stops waiting. "After the decision" is the ordering that is actually guaranteed.
        var approvedAt = DateTimeOffset.UtcNow;

        var approved = await admin.ApproveAccountDeletion(dormant.UserId, ct);

        Assert.That(approved.success, Is.True, approved.error);

        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var finished = await AccountConsoleHarness.DriveDeletionUntilAsync(
            dormant.UserId, AccountDeletionStatusKind.Completed, AccountTimings.ExecutionBudget, ct);

        Assert.That(finished, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the approved erasure did not run, so there is nothing to look for; status was {finished}");

        // A neighbouring fixture's sweep may well land between the erasure finishing and this read —
        // that is what used to lose the row, and the retention window is what makes it survive one.
        // Driving a sweep here rather than hoping for one makes the reconciliation part of the test.
        await RunSweepAsync();

        var page  = await admin.GetAccountDeletionQueue(0, 200, ct);
        var entry = await QueuedAsync(admin, dormant.UserId, ct);

        Assert.That(entry, Is.Not.Null,
            "the approved entry disappeared from the console the moment its erasure anonymised the "
          + "account, which is exactly when an operator needs to see it");

        Assert.Multiple(() =>
        {
            Assert.That(entry!.state, Is.EqualTo(AccountDeletionQueueEntryState.COMPLETED),
                "an erasure that has run is still badged as an approval, which reads as a deletion the "
              + "account holder could still call off");
            Assert.That(entry.username, Does.StartWith("deleted_"),
                "the row should render the tombstone the erasure wrote, not the name it no longer has");
            Assert.That(entry.decidedByOperatorId, Is.EqualTo(OperatorId));
            Assert.That(entry.completedAt, Is.Not.Null.And.GreaterThanOrEqualTo(approvedAt),
                "the row has to say when the erasure finished; it is what the retention window — and so "
              + "the operator's sense of how long this record will be here — is measured from");

            Assert.That(page.entries.Values.Count, Is.LessThanOrEqualTo(page.totalCount),
                "the page renders more rows than it claims to have");
        });

        // The far side of the window. Retention is a reprieve, not a second permanent queue: the entry
        // is the record of a decision that is over, and a worklist that never lets one go accumulates
        // every approval the deployment ever made.
        await Task.Delay(AccountTimings.RetentionAndABit, ct);
        await RunSweepAsync();

        Assert.That(await QueuedAsync(admin, dormant.UserId, ct), Is.Null,
            $"the completed entry outlived its {AccountTimings.DecisionRetention} retention window, so "
          + "nothing ever takes a finished decision off the operator's worklist");
    }

    /// <summary>
    /// A rejected account is reported as held, so the scan can leave it out before it fills its budget.
    /// </summary>
    /// <remarks>
    /// Defect R28, from the queue's side. The scan collects only as many candidates as the queue can
    /// hold and stops there, which is only safe if the accounts the queue would refuse to enqueue are
    /// left out of that count: a declined account is dormant by definition and goes on getting more
    /// dormant, so it sorts to the very top of "longest idle" and would occupy a place there for the
    /// whole decline period — at shipped values, a year of a permanently empty worklist.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_rejected_account_is_reported_to_the_scan_as_held(CancellationToken ct = default)
    {
        var dormant = await ProposeDormantAccountAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var queue = GetGrainFactory().GetGrain<IAccountDeletionQueueGrain>(IAccountDeletionQueueGrain.SingletonId);

        Assert.That(await queue.GetDeclineHoldsAsync(), Does.Not.Contain(dormant.UserId),
            "an account nobody has declined is being held off");

        var rejected = await admin.RejectAccountDeletion(dormant.UserId, ct);

        Assert.That(rejected.success, Is.True, rejected.error);

        Assert.That(await queue.GetDeclineHoldsAsync(), Does.Contain(dormant.UserId),
            "the scan cannot see the refusal, so it spends one of its proposal places on an account "
          + "the queue will refuse to enqueue — for the whole decline period, every pass");
    }

    /// <summary>
    /// An account whose own deletion the scan must skip does not take a place in the proposal set.
    /// </summary>
    /// <remarks>
    /// <para>Defect F2. The scan collects dormant accounts, cuts the list to the queue's ceiling, and
    /// only <em>then</em> asks each survivor's deletion grain the questions the database cannot
    /// answer: is a deletion already scheduled, already running, stranded, or inside the hold its own
    /// holder earned by calling one off. Everything that pass removes had already spent one of the
    /// places the cut kept — and every one of those accounts is dormant by definition and goes on
    /// getting more dormant, so it sorts to the very top of "longest idle" and stays there. Fill the
    /// cut with them and <c>ReconcileAsync</c> is handed an empty proposal set, which does not merely
    /// fail to refill the worklist: reconciliation retires every pending entry it is not given, so the
    /// queue empties and stays empty. At shipped values that is a month per batch of approvals and a
    /// year per self-cancelled deletion.</para>
    ///
    /// <para>The ceiling is five hundred and no test seeds five hundred dormant accounts, so what is
    /// pinned here is the rule the fix rests on rather than the arithmetic that starves: an account
    /// the status pass must skip is not proposed, and a dormant account beside it still is. The cut
    /// itself moved to after that pass — <c>AutoDeleteSchedulerGrain.CollectionCeiling</c> is what
    /// the collection loop trims to now, and <c>MaxProposals</c> is applied to what survives — so the
    /// two together are what keep the worklist filling.</para>
    ///
    /// <para>The declined case is the one driven here because it is the only one that is stable in a
    /// test: a live scheduled deletion would race its own eight-second grace against the sweep, and
    /// an erasure that ran would be filtered out by the soft-delete predicate in the query instead,
    /// which would pass for the wrong reason. It is also the worst of the three in production — the
    /// decline hold is a year, against a thirty-day grace.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task An_account_holding_its_own_refusal_does_not_occupy_a_proposal_place(CancellationToken ct = default)
    {
        var refused = await CreateSessionAsync(ct);
        var dormant = await CreateSessionAsync(ct);

        // Older than the account beside it, so it sorts ahead of it on "longest idle" and would be
        // the one a capped candidate list kept.
        await AccountSeed.BackdateLastLoginAsync(refused.UserId, DateTimeOffset.UtcNow - TimeSpan.FromDays(600), ct: ct);
        await AccountSeed.BackdateLastLoginAsync(dormant.UserId, DateTimeOffset.UtcNow - TimeSpan.FromDays(400), ct: ct);

        var grain     = GetGrainFactory().GetGrain<IAccountDeletionGrain>(refused.UserId);
        var requested = await grain.RequestDeletionAsync(refused.Credentials.password);

        Assert.That(requested.Success, Is.True,
            $"the account could not ask for the deletion it is about to call off: {requested.Error}");

        var cancelled = await grain.CancelDeletionAsync();

        Assert.That(cancelled.Success, Is.True,
            $"the account could not call its own deletion off, so there is no refusal to honour: {cancelled.Error}");

        var status = await grain.GetDeletionStatusAsync();

        Assert.That(status.DeclinedAt, Is.Not.Null,
            "cancelling recorded no refusal, so the scan has nothing to skip and this test would pass "
          + "whatever the scan did");

        await RunSweepAsync();

        var (scope, admin) = Admin();
        await using var _ = scope;

        var squatter  = await QueuedAsync(admin, refused.UserId, ct);
        var proposed  = await QueuedAsync(admin, dormant.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(squatter, Is.Null,
                "an account that told the product it does not want to be deleted is in the operator's "
              + "worklist, and it is the longest-idle account there, so it is the last one a capped "
              + "proposal set would drop");
            Assert.That(proposed, Is.Not.Null,
                "the sweep proposed nothing for an account that is plainly dormant and unencumbered");
        });
    }

    /// <summary>The console's view of one account on the stranded page, paging until it is found.</summary>
    private static async Task<StrandedAccountDeletion?> StrandedAsync(IAdminConsole admin, Guid userId, CancellationToken ct)
    {
        const int size = 200;

        for (var offset = 0; ; offset += size)
        {
            var page = await admin.GetStrandedAccountDeletions(offset, size, ct);

            if (page.entries.Values.FirstOrDefault(entry => entry.userId == userId) is { } found)
                return found;

            if (page.entries.Values.Count < size || offset + page.entries.Values.Count >= page.totalCount)
                return null;
        }
    }

    /// <summary>
    /// Runs one reconciliation that proposes <paramref name="userId"/> again, along with everything the
    /// queue is already holding for a decision.
    /// </summary>
    /// <remarks>
    /// <para>The scan's own filters — a cancelled deletion's <c>DeclinedAt</c>, an account that is no
    /// longer dormant — are the scheduler's rules and not the queue's, and one of them is precisely what
    /// makes the re-proposal case take a year to reach through <c>RunScanAsync</c>. The candidate set is
    /// handed to the queue directly instead.</para>
    ///
    /// <para>Every pending entry currently in the queue is carried in that set, because reconciliation
    /// retires what it is not given and the queue is shared with every other fixture in the run: a
    /// partial set would quietly empty somebody else's worklist. The whole queue is paged for that
    /// reason rather than its first page.</para>
    /// </remarks>
    private async Task ReproposeAsync(IAdminConsole admin, Guid userId, CancellationToken ct)
    {
        const int size = 200;

        var candidates = new List<AccountDeletionCandidate>();

        for (var offset = 0; ; offset += size)
        {
            var page = await admin.GetAccountDeletionQueue(offset, size, ct);

            candidates.AddRange(page.entries.Values
               .Where(entry => entry.state == AccountDeletionQueueEntryState.PENDING && entry.userId != userId)
               .Select(entry => new AccountDeletionCandidate
                {
                    UserId          = entry.userId,
                    LastActivityAt  = entry.lastActivityAt,
                    ThresholdMonths = entry.thresholdMonths,
                    Reason          = entry.reason
                }));

            if (page.entries.Values.Count < size || offset + page.entries.Values.Count >= page.totalCount)
                break;
        }

        candidates.Add(new AccountDeletionCandidate
        {
            UserId          = userId,
            LastActivityAt  = DateTimeOffset.UtcNow - TimeSpan.FromDays(400),
            ThresholdMonths = 12,
            Reason          = AccountDeletionQueueReasons.InactivityDefault
        });

        await GetGrainFactory()
           .GetGrain<IAccountDeletionQueueGrain>(IAccountDeletionQueueGrain.SingletonId)
           .ReconcileAsync(candidates);
    }
}
