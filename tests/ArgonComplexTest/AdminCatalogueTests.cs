namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Grains.Interfaces;
using ArgonContracts;
using ConsoleContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The console's catalogue of things rather than people: item templates and coupons, the tenant
/// directory, spaces, bots, developer teams and internal applications.
/// </summary>
[TestFixture]
public class AdminCatalogueTests : AdminTestBase
{
    protected override Guid SystemOperatorId  => Guid.Parse("00000000-0000-0000-0000-0000000ad301");
    protected override Guid RegularOperatorId => Guid.Parse("00000000-0000-0000-0000-0000000ad302");

    private static CreateItemTemplateInput Template(string templateId, ItemScenarioKind kind, int? ttl = null, params string[] contents)
        => new(templateId, true, false, false, ttl, kind, new IonArray<string>(contents.ToList()));

    private static async Task<Guid> CreateTemplateAsync(IAdminConsole admin, string templateId, ItemScenarioKind kind, CancellationToken ct,
        params Guid[] contents)
    {
        var created = await admin.CreateItemTemplate(Template(templateId, kind, null, contents.Select(c => c.ToString()).ToArray()), ct);

        Assert.That(created.success, Is.True, created.error);

        return created.itemId!.Value;
    }

    // ── Item templates ──────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task CreateItemTemplate_builds_each_kind_of_scenario_and_refuses_a_duplicate_id(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var suffix = Guid.NewGuid().ToString("N")[..12];

        var box     = await admin.CreateItemTemplate(Template($"box_{suffix}", ItemScenarioKind.Box, 3600), ct);
        var premium = await admin.CreateItemTemplate(Template($"prem_{suffix}", ItemScenarioKind.Premium), ct);
        var code    = await admin.CreateItemTemplate(Template($"code_{suffix}", ItemScenarioKind.RedeemCode), ct);

        var duplicate = await admin.CreateItemTemplate(Template($"prem_{suffix}", ItemScenarioKind.Premium), ct);
        var tooLong   = await admin.CreateItemTemplate(Template(new string('x', 300), ItemScenarioKind.None), ct);

        var templates = (await admin.GetItemTemplates(ct)).templates.Values.ToDictionary(t => t.itemId);

        Assert.Multiple(() =>
        {
            Assert.That(box.success && premium.success && code.success, Is.True, $"{box.error} {premium.error} {code.error}");

            Assert.That(templates[box.itemId!.Value].scenarioType, Is.EqualTo(ItemScenarioKind.Box));
            Assert.That(templates[box.itemId!.Value].ttl, Is.EqualTo(3600));
            Assert.That(templates[premium.itemId!.Value].scenarioType, Is.EqualTo(ItemScenarioKind.Premium));
            Assert.That(templates[premium.itemId!.Value].ttl, Is.Null);
            Assert.That(templates[code.itemId!.Value].scenarioType, Is.EqualTo(ItemScenarioKind.RedeemCode));

            Assert.That((duplicate.success, duplicate.error), Is.EqualTo((false, $"Template with ID 'prem_{suffix}' already exists")));
            Assert.That((tooLong.success, tooLong.error), Is.EqualTo((false, "Database error occurred while creating template")),
                "a template id longer than its column is a refusal, not a fault");
        });

        foreach (var id in new[] { box.itemId, premium.itemId, code.itemId })
            Assert.That((await admin.DeleteItemTemplate(id!.Value, ct)).success, Is.True);
    }

    /// <summary>
    /// A box names real templates, is not a copy of an existing box, and holds at least one thing.
    /// </summary>
    /// <remarks>
    /// The last is the fix. A qualifier box with an empty contents list fell through every scenario
    /// arm and was saved as a plain item with no scenario at all — reported as created, listed as
    /// <c>None</c>, and granting it gave the holder nothing to open.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task CreateItemTemplate_checks_what_a_box_holds(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var suffix = Guid.NewGuid().ToString("N")[..12];

        var a = await CreateTemplateAsync(admin, $"in_a_{suffix}", ItemScenarioKind.None, ct);
        var b = await CreateTemplateAsync(admin, $"in_b_{suffix}", ItemScenarioKind.None, ct);
        var c = await CreateTemplateAsync(admin, $"in_c_{suffix}", ItemScenarioKind.None, ct);

        var missingId = Guid.NewGuid();

        var malformed = await admin.CreateItemTemplate(Template($"bad_{suffix}", ItemScenarioKind.QualifierBox, null, a.ToString(), "not-a-guid"), ct);
        var missing   = await admin.CreateItemTemplate(Template($"miss_{suffix}", ItemScenarioKind.QualifierBox, null, a.ToString(), missingId.ToString()), ct);
        var empty     = await admin.CreateItemTemplate(Template($"empty_{suffix}", ItemScenarioKind.QualifierBox), ct);

        var ab = await CreateTemplateAsync(admin, $"box_ab_{suffix}", ItemScenarioKind.QualifierBox, ct, a, b);
        var ac = await CreateTemplateAsync(admin, $"box_ac_{suffix}", ItemScenarioKind.QualifierBox, ct, a, c);

        var templates = (await admin.GetItemTemplates(ct)).templates.Values.ToList();

        Assert.Multiple(() =>
        {
            Assert.That((malformed.success, malformed.error), Is.EqualTo((false, "One or more box content IDs have invalid format")));
            Assert.That((missing.success, missing.error), Is.EqualTo((false, $"Reference items not found: {missingId}")));
            Assert.That((empty.success, empty.error), Is.EqualTo((false, "A qualifier box needs at least one item")),
                "a box with nothing in it was created");
            Assert.That(templates.Select(t => t.templateId), Does.Not.Contain($"empty_{suffix}"));

            Assert.That(templates.Single(t => t.itemId == ab).boxContents.Values.Select(x => x.itemId), Is.EquivalentTo(new[] { a, b }));
            Assert.That(templates.Single(t => t.itemId == ac).boxContents.Values.Select(x => x.itemId), Is.EquivalentTo(new[] { a, c }),
                "a box that shares one item with another is a different box, not a duplicate");
        });

        foreach (var id in new[] { ab, ac, a, b, c })
            Assert.That((await admin.DeleteItemTemplate(id, ct)).success, Is.True);
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteItemFromUserInventory_touches_only_the_named_holders_own_copy(CancellationToken ct = default)
    {
        var holder = await CreateSessionAsync(ct);
        var other  = await CreateSessionAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var templateId = $"inv_{Guid.NewGuid():N}"[..24];
        var template   = await CreateTemplateAsync(admin, templateId, ItemScenarioKind.None, ct);

        Assert.That((await admin.GrantItem(holder.UserId, template, ct)).success, Is.True);

        var copy          = (await admin.GetUserCard(holder.UserId, ct)).items.Values.Single(i => i.templateId == templateId).itemId;
        var unknownHolder = Guid.NewGuid();
        var unknownItem   = Guid.NewGuid();

        var noSuchUser  = await admin.DeleteItemFromUserInventory(unknownHolder, copy, ct);
        var noSuchItem  = await admin.DeleteItemFromUserInventory(holder.UserId, unknownItem, ct);
        var theTemplate = await admin.DeleteItemFromUserInventory(holder.UserId, template, ct);
        var notTheirs   = await admin.DeleteItemFromUserInventory(other.UserId, copy, ct);

        var stillHeld = (await admin.GetUserCard(holder.UserId, ct)).items.Values.Any(i => i.itemId == copy);
        var stillListed = (await admin.GetItemTemplates(ct)).templates.Values.Any(t => t.itemId == template);

        Assert.Multiple(() =>
        {
            Assert.That((noSuchUser.success, noSuchUser.error), Is.EqualTo((false, $"User {unknownHolder} not found")));
            Assert.That((noSuchItem.success, noSuchItem.error), Is.EqualTo((false, $"Item {unknownItem} not found")));
            Assert.That((theTemplate.success, theTemplate.error),
                Is.EqualTo((false, $"Cannot delete reference template {template}, use DeleteItemTemplate instead")));
            Assert.That((notTheirs.success, notTheirs.error), Is.EqualTo((false, $"Item {copy} does not belong to user {other.UserId}")));
            Assert.That(stillHeld, Is.True, "a refused removal took the item anyway");
            Assert.That(stillListed, Is.True, "a refused removal took the template anyway");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteItemTemplate_refuses_a_holders_copy_and_a_template_a_coupon_hands_out(CancellationToken ct = default)
    {
        var holder = await CreateSessionAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var templateId = $"cpn_{Guid.NewGuid():N}"[..24];
        var template   = await CreateTemplateAsync(admin, templateId, ItemScenarioKind.None, ct);

        Assert.That((await admin.GrantItem(holder.UserId, template, ct)).success, Is.True);

        var copy = (await admin.GetUserCard(holder.UserId, ct)).items.Values.Single(i => i.templateId == templateId).itemId;

        var coupon = await admin.CreateCoupon(new CreateCouponInput($"CPN{Guid.NewGuid():N}"[..12].ToUpperInvariant(), "hands out the template",
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), 1, template), ct);

        Assert.That(coupon.success, Is.True, coupon.error);

        var notATemplate = await admin.DeleteItemTemplate(copy, ct);
        var inACoupon    = await admin.DeleteItemTemplate(template, ct);

        Assert.Multiple(() =>
        {
            Assert.That((notATemplate.success, notATemplate.error),
                Is.EqualTo((false, $"DeleteItemTemplate failed: item {copy} is not a reference template")));
            Assert.That((inACoupon.success, inACoupon.error),
                Is.EqualTo((false, $"DeleteItemTemplate failed: template {template} is used in coupons")),
                "deleting it would leave the coupon redeeming for nothing");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task CreateCoupon_for_an_item_that_does_not_exist_is_refused(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var code   = $"MISS{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var result = await admin.CreateCoupon(new CreateCouponInput(code, "points at nothing",
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), 1, Guid.NewGuid()), ct);

        var coupons = await admin.GetCoupons(ct);

        Assert.Multiple(() =>
        {
            Assert.That(result.success, Is.False);
            Assert.That(result.couponId, Is.Null);
            Assert.That(coupons.coupons.Values.Select(c => c.code), Does.Not.Contain(code));
        });
    }

    // ── Tenant directory ────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Tenant_changes_refuse_an_unknown_tenant_and_verification_needs_a_system_operator(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var nowhere = Guid.NewGuid();

        var update = await admin.UpdateTenant(new UpdateTenantInput(nowhere, "https://nowhere.test", null, null), ct);
        var verify = await admin.SetTenantVerified(nowhere, true, ct);
        var delete = await admin.DeleteTenant(nowhere, ct);

        var domain  = $"r{Guid.NewGuid():N}.test.local";
        var created = await admin.CreateTenant(new CreateTenantInput(domain, $"https://{domain}", null, null, null), ct);

        var (regularScope, regular) = AdminAs(RegularOperatorId, RegularOperatorEmail);
        await using var __ = regularScope;

        var byRegular = await regular.SetTenantVerified(created.tenantId!.Value, true, ct);
        var tenant    = (await regular.GetTenantDirectory(ct)).tenants.Values.Single(t => t.tenantId == created.tenantId);

        Assert.Multiple(() =>
        {
            foreach (var (name, result) in new[] { ("update", update), ("verify", verify), ("delete", delete) })
                Assert.That((result.success, result.error), Is.EqualTo((false, "Tenant not found")), name);

            Assert.That((byRegular.success, byRegular.error), Is.EqualTo((false, "Only system operators can verify tenants")));
            Assert.That(tenant.isVerified, Is.False);
        });
    }

    /// <summary>
    /// Moving a verified tenant to another instance takes its verification away until a system operator
    /// gives it back.
    /// </summary>
    /// <remarks>
    /// <para>A verified tenant is what sign-in discovery trusts: every address under the domain is sent
    /// to its instance URL to sign in. Verifying is a system-operator step, but editing is open to any
    /// operator — and the edit changed the URL and kept the verification. So any operator could point a
    /// verified company's sign-ins at a server of their choosing, and the check that exists to stop that
    /// was never asked.</para>
    ///
    /// <para>Editing the organisation name or the notes changes nothing discovery serves, and keeps
    /// it.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Moving_a_verified_tenant_to_another_instance_takes_its_verification_away(CancellationToken ct = default)
    {
        var (scope, admin) = Admin();
        await using var _ = scope;

        var domain    = $"v{Guid.NewGuid():N}.test.local";
        var original  = $"https://sso.{domain}";
        var created   = await admin.CreateTenant(new CreateTenantInput(domain, original, "Acme", null, null), ct);
        var tenantId  = created.tenantId!.Value;
        var directory = GetGrainFactory().GetGrain<IIdentityDirectoryGrain>(Guid.Empty);

        Assert.That((await admin.SetTenantVerified(tenantId, true, ct)).success, Is.True);

        var (regularScope, regular) = AdminAs(RegularOperatorId, RegularOperatorEmail);
        await using var __ = regularScope;

        var renamed        = await regular.UpdateTenant(new UpdateTenantInput(tenantId, original, "Acme Corp", "renamed"), ct);
        var afterRename    = (await regular.GetTenantDirectory(ct)).tenants.Values.Single(t => t.tenantId == tenantId);
        var resolvedBefore = await directory.ResolveTenantInstanceAsync(domain);

        var moved         = await regular.UpdateTenant(new UpdateTenantInput(tenantId, "https://elsewhere.example", "Acme Corp", null), ct);
        var afterMove     = (await regular.GetTenantDirectory(ct)).tenants.Values.Single(t => t.tenantId == tenantId);
        var resolvedAfter = await directory.ResolveTenantInstanceAsync(domain);

        Assert.Multiple(() =>
        {
            Assert.That(renamed.success && moved.success, Is.True, $"{renamed.error} {moved.error}");
            Assert.That((afterRename.isVerified, afterRename.orgName, afterRename.notes), Is.EqualTo((true, "Acme Corp", "renamed")));
            Assert.That(resolvedBefore, Is.EqualTo(original));
            Assert.That(afterMove.instanceUrl, Is.EqualTo("https://elsewhere.example"));
            Assert.That(afterMove.isVerified, Is.False, "the tenant was moved and kept a verification nobody gave the new instance");
            Assert.That(resolvedAfter, Is.Null, "sign-in discovery sends the domain to an instance no system operator verified");
        });
    }

    // ── Spaces ──────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Space_flags_are_set_on_the_space_and_refused_for_one_that_does_not_exist(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var created = await owner.Users.CreateSpace(new CreateServerRequest("Flagged", "fixture", string.Empty), ct);
        var spaceId = ((SuccessCreateSpace)created).space.spaceId;

        var (scope, admin) = Admin();
        await using var _ = scope;

        var community = await admin.SetSpaceCommunity(spaceId, true, ct);
        var official  = await admin.SetSpaceOfficial(spaceId, true, ct);
        var card      = await admin.GetSpaceCard(spaceId, ct);
        var nowhere   = await admin.SetSpaceOfficial(Guid.NewGuid(), true, ct);
        var blank     = await admin.SearchSpace("  ", ct);

        Assert.Multiple(async () =>
        {
            Assert.That(community.success && official.success, Is.True, $"{community.error} {official.error}");
            Assert.That((card.isCommunity, card.isOfficial), Is.EqualTo((true, (bool?)true)));
            Assert.That((nowhere.success, nowhere.error), Is.EqualTo((false, "Space not found")));
            Assert.That((blank.found, blank.matchedBy), Is.EqualTo((false, SpaceSearchMatchKind.None)));
            Assert.That(await AuditAsync(admin, "SetSpaceOfficial", spaceId.ToString(), ct), Has.Count.EqualTo(1));
        });
    }

    // ── Bots ────────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Bot_buttons_change_the_bot_and_refuse_one_that_does_not_exist(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var teamId = await SeedTeamAsync(owner.UserId, "Bot Buttons", ct);
        var (appId, _, username) = await SeedBotAsync(teamId, "Button Bot", false, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var byId      = await admin.SearchBot(appId.ToString(), ct);
        var byOtherId = await admin.SearchBot(Guid.NewGuid().ToString(), ct);
        var blank     = await admin.SearchBot(" ", ct);

        var verified  = await admin.SetBotVerified(appId, true, ct);
        var capped    = await admin.SetBotMaxSpaces(appId, 42, ct);
        var suspended = await admin.SetBotLifecycleState(appId, AdminBotLifecycleState.Suspended, ct);
        var internal_ = await admin.SetBotInternalApp(appId, true, ct);

        var card = await admin.GetBotCard(appId, ct);

        var nowhere = Guid.NewGuid();
        var refusals = new[]
        {
            await admin.SetBotVerified(nowhere, true, ct),
            await admin.SetBotMaxSpaces(nowhere, 1, ct),
            await admin.SetBotLifecycleState(nowhere, AdminBotLifecycleState.Published, ct)
        };
        var noApp = await admin.SetBotInternalApp(nowhere, true, ct);

        Assert.Multiple(async () =>
        {
            Assert.That((byId.found, byId.bot?.appId, byId.bot?.username), Is.EqualTo((true, (Guid?)appId, (string?)username)));
            Assert.That(byOtherId.found, Is.False, "an unknown id fell through to the name searches and matched something");
            Assert.That(blank.found, Is.False);

            Assert.That(new[] { verified, capped, suspended, internal_ }.All(r => r.success), Is.True);
            Assert.That(card.isVerified, Is.True);
            Assert.That(card.maxSpaces, Is.EqualTo(42));
            Assert.That(card.lifecycleState, Is.EqualTo(AdminBotLifecycleState.Suspended));
            Assert.That(card.isInternalApp, Is.True);

            Assert.That(refusals.Select(r => (r.success, r.error)), Is.All.EqualTo((false, "Bot not found")));
            Assert.That((noApp.success, noApp.error), Is.EqualTo((false, "App not found")));

            foreach (var action in new[] { "SetBotVerified", "SetBotMaxSpaces", "SetBotLifecycleState" })
                Assert.That(await AuditAsync(admin, action, appId.ToString(), ct), Has.Count.EqualTo(1), action);
            Assert.That(await AuditAsync(admin, "SetBotVerified", nowhere.ToString(), ct), Is.Empty, "a refusal was audited");
        });
    }

    // ── Teams and internal applications ─────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_team_is_found_by_id_or_name_and_its_card_lists_members_and_applications(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);
        var name   = $"Card Team {Guid.NewGuid():N}"[..24];
        var teamId = await SeedTeamAsync(owner.UserId, name, ct);

        var (plainId, _)  = await SeedAppAsync(teamId, "Plain App", true, ct);
        var (botId, _, _) = await SeedBotAsync(teamId, "Team Bot", false, ct);
        var clientId      = Guid.NewGuid();

        await using (var db = await NewDbAsync(ct))
        {
            db.MemberTeamEntities.Add(new DevTeamMemberEntity
            {
                TeamId = teamId, UserId = member.UserId, JoinedAt = DateTime.UtcNow, Claims = ["apps:read", "apps:write"]
            });

            db.AppEntities.Add(new ClientAppEntity
            {
                AppId              = clientId,
                TeamId             = teamId,
                Name               = "Desktop Client",
                ClientId           = $"client_{clientId:N}",
                ClientSecret       = Guid.NewGuid().ToString("N"),
                AppType            = DevAppType.Application,
                Platform           = ClientAppPlatformKind.WindowsDesktop,
                RateLimitPerMinute = 60,
                IsVerified         = true,
                RequiredScopes     = [],
                AllowedRedirects   = []
            });

            await db.SaveChangesAsync(ct);

            await db.BotEntities.Where(b => b.AppId == botId).ExecuteUpdateAsync(s => s.SetProperty(b => b.IsVerified, true), ct);
        }

        var (scope, admin) = Admin();
        await using var _ = scope;

        var byId      = await admin.SearchTeam(teamId.ToString(), ct);
        var byName    = await admin.SearchTeam($"  {name.ToUpperInvariant()} ", ct);
        var byOtherId = await admin.SearchTeam(Guid.NewGuid().ToString(), ct);
        var blank     = await admin.SearchTeam("", ct);
        var card      = await admin.GetTeamCard(teamId, ct);

        var members = card.members.Values.ToDictionary(m => m.userId);
        var apps    = card.apps.Values.ToDictionary(a => a.appId);

        Assert.Multiple(() =>
        {
            Assert.That((byId.found, byId.team?.memberCount, byId.team?.appCount), Is.EqualTo((true, (int?)2, (int?)3)));
            Assert.That(byName.team?.teamId, Is.EqualTo(teamId), "the name search is case-insensitive");
            Assert.That(byOtherId.found, Is.False);
            Assert.That(blank.found, Is.False);

            Assert.That((card.name, card.owner.userId), Is.EqualTo((name, owner.UserId)));
            Assert.That(members.Keys, Is.EquivalentTo(new[] { owner.UserId, member.UserId }));
            Assert.That(members[owner.UserId].isOwner, Is.True);
            Assert.That(members[member.UserId].claims.Values, Is.EqualTo(new[] { "apps:read", "apps:write" }));
            Assert.That(members[member.UserId].username, Is.EqualTo(member.Credentials.username));

            Assert.That(apps.Keys, Is.EquivalentTo(new[] { plainId, botId, clientId }));
            Assert.That((apps[plainId].appType, apps[plainId].isInternalApp, apps[plainId].isVerified),
                Is.EqualTo((AdminDevAppType.Application, true, false)));
            Assert.That((apps[botId].appType, apps[botId].isVerified), Is.EqualTo((AdminDevAppType.Bot, true)));
            Assert.That(apps[clientId].isVerified, Is.True, "a verified client application reads as verified");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task SearchInternalApps_matches_an_id_a_name_a_client_id_or_a_bot_username(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var teamId = await SeedTeamAsync(owner.UserId, $"Internal {suffix}", ct);

        var (appId, clientId)    = await SeedAppAsync(teamId, $"Ops Console {suffix}", true, ct);
        var (publicId, _)        = await SeedAppAsync(teamId, $"Ops Public {suffix}", false, ct);
        var (botId, _, username) = await SeedBotAsync(teamId, "Ops Bot", true, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        async Task<Guid[]> Found(string query) => (await admin.SearchInternalApps(query, ct)).apps.Values.Select(a => a.appId).ToArray();

        var blank      = await Found("   ");
        var byId       = await admin.SearchInternalApps(appId.ToString(), ct);
        var byName     = await Found($"OPS CONSOLE {suffix}".ToUpperInvariant());
        var byClient   = await Found(clientId[^12..]);
        var byUsername = await Found(username[^10..]);
        var byFragment = await Found($"ops public {suffix}");
        var byOtherId  = await Found(Guid.NewGuid().ToString());

        var found = byId.apps.Values.SingleOrDefault();

        Assert.Multiple(() =>
        {
            Assert.That(blank, Is.Empty);
            Assert.That((found?.appId, found?.clientId, found?.teamName), Is.EqualTo(((Guid?)appId, (string?)clientId, (string?)$"Internal {suffix}")));
            Assert.That(byName, Is.EqualTo(new[] { appId }));
            Assert.That(byClient, Is.EqualTo(new[] { appId }));
            Assert.That(byUsername, Is.EqualTo(new[] { botId }), "an internal bot is found by the username it speaks as");
            Assert.That(byFragment, Does.Not.Contain(publicId), "an application that is not internal was offered");
            Assert.That(byOtherId, Is.Empty);
        });
    }
}
