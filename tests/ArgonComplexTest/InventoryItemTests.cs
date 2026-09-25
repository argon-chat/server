namespace ArgonComplexTest.Tests;

using Argon.Api.Entities.Data;
using Argon.Api.Grains.Interfaces;
using Argon.Entities;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Items after they are given: cases opened, gifts used, codes redeemed, badges earned — and what the
/// owner is told about each through the inventory notifications.
/// </summary>
/// <remarks>
/// Templates and cases are made the way the operator console makes them: reference rows owned by the
/// system user, copied into somebody's inventory by <c>GiveItemFor</c>. Every template id carries a
/// suffix of its own, because <c>GetReferencesItemsAsync</c> lists every reference row in the database.
/// </remarks>
[TestFixture]
public class InventoryItemTests : TestBase
{
    private IInventoryGrain Inventory => GetGrainFactory().GetGrain<IInventoryGrain>(Guid.NewGuid());

    private static string Template(string name) => $"cov_{name}_{Guid.NewGuid():N}"[..40];

    private async Task<ApplicationDbContext> DbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    private async Task<Guid> ReferenceAsync(string templateId, bool badge = false, CancellationToken ct = default)
    {
        var id = await Inventory.CreateReferenceItem(templateId, false, true, badge, ct);

        Assert.That(id, Is.Not.Null);

        return id!.Value;
    }

    /// <summary>A case template holding several reference items, as the console writes one.</summary>
    private async Task<Guid> MultiCaseAsync(string templateId, IEnumerable<Guid> contents, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);

        var item = new ArgonItemEntity
        {
            Id            = Guid.NewGuid(),
            TemplateId    = templateId,
            IsReference   = true,
            IsUsable      = true,
            OwnerId       = UserEntity.SystemUser,
            UseVector     = ItemUseVector.QualifierBox,
            Scenario      = new MultipleQualifierBox { Key = Guid.NewGuid(), ReferenceItemIds = contents.ToList() },
            CreatedAt     = DateTimeOffset.UtcNow
        };

        db.Items.Add(item);
        await db.SaveChangesAsync(ct);

        return item.Id;
    }

    /// <summary>Gives <paramref name="session"/> a copy of a template and returns the copy's instance id.</summary>
    private async Task<Guid> GiveAsync(TestUserSession session, Guid referenceId, CancellationToken ct)
    {
        var before = (await session.Inventory.GetMyInventoryItems(ct)).Values.Select(i => i.instanceId).ToHashSet();

        Assert.That(await Inventory.GiveItemFor(session.UserId, referenceId, ct), Is.True);

        var items = (await session.Inventory.GetMyInventoryItems(ct)).Values;

        return items.Single(i => !before.Contains(i.instanceId)).instanceId;
    }

    private static async Task ClearNotificationsAsync(TestUserSession session, CancellationToken ct)
    {
        var unread = (await session.Inventory.GetNotifications(ct)).Values.Select(n => n.inventoryItemId).ToArray();

        await session.Inventory.MarkSeen(new IonArray<Guid>(unread), ct);
    }

    private static async Task<IReadOnlyList<InventoryNotification>> NotificationsAsync(TestUserSession session, int atLeast, CancellationToken ct)
    {
        // MarkSeen is one-way, so a clear issued just before can still be landing.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (true)
        {
            var unread = (await session.Inventory.GetNotifications(ct)).Values;

            if (unread.Count >= atLeast || DateTimeOffset.UtcNow > deadline)
                return unread;

            await Task.Delay(100, ct);
        }
    }

    // ── The reference catalogue ──────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task The_reference_catalogue_shows_what_each_case_holds(CancellationToken ct = default)
    {
        var sword  = await ReferenceAsync(Template("sword"), ct: ct);
        var shield = await ReferenceAsync(Template("shield"), ct: ct);

        var swordCase = await Inventory.CreateCaseForReferenceItem(sword, Template("sword_case"), ct);
        var bundle    = await MultiCaseAsync(Template("bundle"), [sword, shield, Guid.NewGuid()], ct);

        Assert.That(swordCase, Is.Not.Null);
        Assert.That(await Inventory.CreateCaseForReferenceItem(Guid.NewGuid(), Template("empty_case"), ct), Is.Null,
            "a case was made around a template that does not exist");

        var catalogue = await Inventory.GetReferencesItemsAsync(ct);

        IEnumerable<Guid> Contents(Guid referenceId)
            => catalogue.Single(entry => entry.item.instanceId == referenceId).containedItems.Values.Select(i => i.instanceId);

        Assert.Multiple(() =>
        {
            Assert.That(Contents(swordCase!.Value), Is.EqualTo(new[] { sword }));

            // A content id that names nothing is left out rather than failing the whole listing.
            Assert.That(Contents(bundle), Is.EquivalentTo(new[] { sword, shield }));
            Assert.That(Contents(sword), Is.Empty);

            var caseEntry = catalogue.Single(entry => entry.item.instanceId == swordCase!.Value).item;
            Assert.That(caseEntry.usable, Is.True);
            Assert.That(caseEntry.usableVector, Is.EqualTo(ItemUseVector.QualifierBox));
        });
    }

    // ── Opening cases ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The notification for what came out of a case used to carry the <em>case's</em> template id
    /// beside the new item's instance id, so the client announced "you got a case" for the sword it
    /// had just unpacked.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Opening_a_case_grants_its_item_under_the_items_own_name(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);

        var swordTemplate = Template("sword");
        var sword         = await ReferenceAsync(swordTemplate, badge: true, ct: ct);
        var swordCase     = await Inventory.CreateCaseForReferenceItem(sword, Template("sword_case"), ct);
        var box           = await GiveAsync(owner, swordCase!.Value, ct);

        await ClearNotificationsAsync(owner, ct);

        Assert.That(await owner.Inventory.UseItem(box, ct), Is.True);

        var items = (await owner.Inventory.GetMyInventoryItems(ct)).Values;
        var won   = items.SingleOrDefault(i => i.id == swordTemplate);

        Assert.Multiple(() =>
        {
            Assert.That(items.Select(i => i.instanceId), Does.Not.Contain(box), "the opened case is still in the inventory");
            Assert.That(won, Is.Not.Null, "nothing came out of the case");
        });

        var unread = await NotificationsAsync(owner, 1, ct);

        Assert.That(unread.Select(n => (n.inventoryItemId, n.id)), Is.EqualTo(new[] { (won!.instanceId, swordTemplate) }));
        Assert.That((await owner.Users.GetMyProfile(ct)).badges.Values, Does.Contain(swordTemplate));

        // Opened is opened: the second attempt finds no case.
        Assert.That(await owner.Inventory.UseItem(box, ct), Is.False);
        Assert.That((await owner.Inventory.GetMyInventoryItems(ct)).Values.Count(i => i.id == swordTemplate), Is.EqualTo(1));
    }

    [Test, CancelAfter(120_000)]
    public async Task Opening_a_bundle_grants_everything_in_it_and_the_badges_they_carry(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);

        var medalTemplate = Template("medal");
        var cloakTemplate = Template("cloak");
        var medal         = await ReferenceAsync(medalTemplate, badge: true, ct: ct);
        var cloak         = await ReferenceAsync(cloakTemplate, ct: ct);
        var bundle        = await MultiCaseAsync(Template("bundle"), [medal, cloak], ct);
        var box           = await GiveAsync(owner, bundle, ct);

        await ClearNotificationsAsync(owner, ct);

        Assert.That(await owner.Inventory.UseItem(box, ct), Is.True);

        var items   = (await owner.Inventory.GetMyInventoryItems(ct)).Values;
        var unread  = await NotificationsAsync(owner, 2, ct);
        var profile = await owner.Users.GetMyProfile(ct);

        Assert.Multiple(() =>
        {
            Assert.That(items.Select(i => i.id), Is.EquivalentTo(new[] { medalTemplate, cloakTemplate }));
            Assert.That(unread.Select(n => n.id), Is.EquivalentTo(new[] { medalTemplate, cloakTemplate }));
            Assert.That(profile.badges.Values, Does.Contain(medalTemplate));
            Assert.That(profile.badges.Values, Does.Not.Contain(cloakTemplate));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_case_with_nothing_left_to_give_stays_closed(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);

        var withdrawn = await ReferenceAsync(Template("withdrawn"), ct: ct);
        var lonelyCase = await Inventory.CreateCaseForReferenceItem(withdrawn, Template("lonely_case"), ct);
        var hollow     = await MultiCaseAsync(Template("hollow"), [Guid.NewGuid()], ct);
        var empty      = await MultiCaseAsync(Template("empty"), [], ct);

        var single = await GiveAsync(owner, lonelyCase!.Value, ct);
        var many   = await GiveAsync(owner, hollow, ct);
        var none   = await GiveAsync(owner, empty, ct);

        // The operator withdraws the template the case was made around.
        await using (var db = await DbAsync(ct))
        {
            await db.Items.Where(i => i.Id == withdrawn).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsDeleted, true), ct);
        }

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await owner.Inventory.UseItem(single, ct), Is.False);
            Assert.That(await owner.Inventory.UseItem(many, ct), Is.False);
            Assert.That(await owner.Inventory.UseItem(none, ct), Is.False);
        });

        // Not eaten: a case that could not be opened is still there to open once it has contents again.
        Assert.That((await owner.Inventory.GetMyInventoryItems(ct)).Values.Select(i => i.instanceId),
            Is.SupersetOf(new[] { single, many, none }));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_gifted_Ultima_starts_the_subscription_when_it_is_used(CancellationToken ct = default)
    {
        var sender    = await CreateSessionAsync(ct);
        var recipient = await CreateSessionAsync(ct);

        Assert.That(await Inventory.GiveUltimaGiftAsync(recipient.UserId, "ultima_annual", 365, sender.UserId, "enjoy", ct), Is.True);

        var gift = (await recipient.Inventory.GetMyInventoryItems(ct)).Values.Single(i => i.id == "gift_ultima_annual");

        Assert.Multiple(() =>
        {
            Assert.That(gift.usable, Is.True);
            Assert.That(gift.receivedFrom, Is.EqualTo(sender.UserId));
            Assert.That(gift.usableVector, Is.EqualTo(ItemUseVector.Premium));
        });

        Assert.That(await recipient.Ultima.GetMySubscription(ct), Is.Null, "the gift was not supposed to act before it is used");

        Assert.That(await recipient.Inventory.UseItem(gift.instanceId, ct), Is.True);

        var subscription = await recipient.Ultima.GetMySubscription(ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(subscription, Is.Not.Null);
            Assert.That(subscription!.tier, Is.EqualTo(UltimaPlan.Annual));
            Assert.That(subscription.status, Is.EqualTo(UltimaSubscriptionStatus.Active));
            Assert.That(subscription.expiresAt, Is.EqualTo(DateTimeOffset.UtcNow.AddDays(365)).Within(TimeSpan.FromMinutes(1)));
            Assert.That((await recipient.Inventory.GetMyInventoryItems(ct)).Values.Select(i => i.instanceId),
                Does.Not.Contain(gift.instanceId), "a used gift can be used again");
        });

        await using var db = await DbAsync(ct);

        var row = await db.UltimaSubscriptions.AsNoTracking().SingleAsync(s => s.UserId == recipient.UserId, ct);

        Assert.That(row.ActivatedFromItemId, Is.EqualTo(gift.instanceId), "the subscription does not say which gift started it");

        // A month rather than a year, the other way a gift is written.
        Assert.That(await Inventory.GiveUltimaGiftAsync(sender.UserId, "ultima_monthly", 30, recipient.UserId, null, ct), Is.True);

        var returned = (await sender.Inventory.GetMyInventoryItems(ct)).Values.Single(i => i.id == "gift_ultima_monthly");

        Assert.That(await sender.Inventory.UseItem(returned.instanceId, ct), Is.True);

        var senderSubscription = await sender.Ultima.GetMySubscription(ct);

        Assert.Multiple(() =>
        {
            Assert.That(senderSubscription!.tier, Is.EqualTo(UltimaPlan.Monthly));
            Assert.That(senderSubscription.expiresAt, Is.EqualTo(DateTimeOffset.UtcNow.AddDays(30)).Within(TimeSpan.FromMinutes(1)));
        });
    }

    // ── Badges and codes ─────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_badge_item_puts_its_badge_on_the_profile_once(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);

        var trophyTemplate = Template("trophy");
        var trophy         = await ReferenceAsync(trophyTemplate, badge: true, ct: ct);

        Assert.That(await Inventory.GiveItemFor(owner.UserId, trophy, ct), Is.True);
        Assert.That(await Inventory.GiveItemFor(owner.UserId, trophy, ct), Is.True);
        Assert.That(await Inventory.GiveItemFor(owner.UserId, Guid.NewGuid(), ct), Is.False, "a template that does not exist was handed out");

        var profile = await owner.Users.GetMyProfile(ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(profile.badges.Values.Count(b => b == trophyTemplate), Is.EqualTo(1));
            Assert.That((await owner.Inventory.GetMyInventoryItems(ct)).Values.Count(i => i.id == trophyTemplate), Is.EqualTo(2));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Redeeming_a_code_grants_its_item_its_badge_and_a_notification(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);

        var pinTemplate = Template("pin");
        var pin         = await ReferenceAsync(pinTemplate, badge: true, ct: ct);
        var code        = $"COV{Guid.NewGuid():N}"[..16].ToUpperInvariant();

        await using (var db = await DbAsync(ct))
        {
            db.Coupons.Add(new ArgonCouponEntity
            {
                Id                    = Guid.NewGuid(),
                Code                  = code,
                ValidFrom             = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo               = DateTimeOffset.UtcNow.AddDays(1),
                MaxRedemptions        = 10,
                IsActive              = true,
                ReferenceItemEntityId = pin
            });

            await db.SaveChangesAsync(ct);
        }

        Assert.That(await owner.Inventory.RedeemCode(code, ct), Is.InstanceOf<SuccessRedeem>());

        var items   = (await owner.Inventory.GetMyInventoryItems(ct)).Values;
        var unread  = await NotificationsAsync(owner, 1, ct);
        var profile = await owner.Users.GetMyProfile(ct);
        var granted = items.Single(i => i.id == pinTemplate);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(unread.Select(n => n.inventoryItemId), Does.Contain(granted.instanceId));
            Assert.That(profile.badges.Values, Does.Contain(pinTemplate));

            // The same code twice is one pin.
            Assert.That(((FailedRedeem)await owner.Inventory.RedeemCode(code, ct)).error, Is.EqualTo(RedeemError.ALREADY));
            Assert.That((await owner.Inventory.GetMyInventoryItems(ct)).Values.Count(i => i.id == pinTemplate), Is.EqualTo(1));
        });
    }
}
