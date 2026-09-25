namespace ArgonComplexTest.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Argon.Entities;
using Argon.Grains;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// An Ultima subscription over its whole life — renewed, cancelled, refunded, lapsed — and the
/// payment history and boosts that hang off it. <see cref="UltimaTests"/> covers the happy path of
/// each call; this fixture is about what happens when the calls arrive in the order real billing
/// sends them.
/// </summary>
/// <remarks>
/// <para>Nothing here resets the shared <see cref="FakeXsollaService"/>: its attribute log is cleared by
/// <see cref="UltimaTests"/> and <see cref="XsollaWebHookTests"/> while this fixture may be running,
/// so every assertion reads the database or the grain instead.</para>
///
/// <para>The grace tests shorten <see cref="UltimaGrain.RenewalGrace"/>, which is process-wide. Only a
/// subscription Xsolla renews whose paid period is already over ever reads it, and no other fixture
/// makes one, so a few seconds of a shorter grace touch nothing but the users made here.</para>
/// </remarks>
[TestFixture]
public class UltimaLifecycleTests : TestBase
{
    private IUltimaGrain Ultima(Guid userId) => GetGrainFactory().GetGrain<IUltimaGrain>(userId);

    private async Task<ApplicationDbContext> DbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    private async Task<bool> HasActiveUltimaAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);

        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.HasActiveUltima).SingleAsync(ct);
    }

    private async Task<List<UltimaSubscriptionEntity>> SubscriptionsAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);

        return await db.UltimaSubscriptions.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);
    }

    private async Task<HttpStatusCode> PostWebhookAsync(object payload, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/xsolla/webhook")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", "Signature test-signature");

        return (await HttpClient.SendAsync(request, ct)).StatusCode;
    }

    private async Task<UltimaSubscriptionStatus?> StatusAsync(Guid userId, CancellationToken ct)
        => (await Ultima(userId).GetSubscriptionAsync(ct))?.status;

    /// <summary>Moves the end of the user's subscription, as time passing would.</summary>
    private async Task EndsAtAsync(Guid userId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);

        await db.UltimaSubscriptions
           .Where(s => s.UserId == userId && s.Status != UltimaStatus.Expired)
           .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, expiresAt), ct);
    }

    private static async Task WithRenewalGraceAsync(TimeSpan grace, Func<Task> test)
    {
        var shipped = UltimaGrain.RenewalGrace;
        UltimaGrain.RenewalGrace = grace;

        try
        {
            await test();
        }
        finally
        {
            UltimaGrain.RenewalGrace = shipped;
        }
    }

    private static readonly TimeSpan ReminderDeadline = TimeSpan.FromSeconds(30);

    // ── Payment history ──────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task The_payment_history_lists_each_payment_once_newest_first_with_its_refund(CancellationToken ct = default)
    {
        var buyer = await CreateSessionAsync(ct);
        var grain = Ultima(buyer.UserId);

        var subscriptionTx = $"tx-sub-{Guid.NewGuid():N}";
        var boostTx        = $"tx-boost-{Guid.NewGuid():N}";

        await grain.SaveTransactionAsync(subscriptionTx, "subscription", "ultima_monthly", null, null, null, "9.99", "USD",
            "4242", "Visa", 777, ct);
        await Task.Delay(20, ct);
        await grain.SaveTransactionAsync(boostTx, "boost_pack", null, "boost_pack_3", 3, null, "12.99", "USD", ct: ct);

        // Xsolla retries a webhook it did not see acknowledged; the retry must not be a second payment.
        await grain.SaveTransactionAsync(subscriptionTx, "subscription", "ultima_monthly", null, null, null, "0.01", "EUR", ct: ct);

        await grain.MarkTransactionRefundedAsync(subscriptionTx, ct);

        var history = (await buyer.Ultima.GetTransactionHistory(ct)).Values;

        Assert.That(history.Select(t => t.paymentId), Is.EqualTo(new[] { boostTx, subscriptionTx }));

        var boost        = history[0];
        var subscription = history[1];

        Assert.Multiple(() =>
        {
            Assert.That(boost.status, Is.EqualTo("done"));
            Assert.That(boost.boostPackType, Is.EqualTo("boost_pack_3"));
            Assert.That(boost.boostCount, Is.EqualTo(3));

            Assert.That(subscription.status, Is.EqualTo("refunded"));
            Assert.That(subscription.amount, Is.EqualTo("9.99"), "the retried webhook overwrote the first payment");
            Assert.That(subscription.currency, Is.EqualTo("USD"));
            Assert.That(subscription.planExternalId, Is.EqualTo("ultima_monthly"));
            Assert.That(subscription.cardSuffix, Is.EqualTo("4242"));
            Assert.That(subscription.cardBrand, Is.EqualTo("Visa"));
            Assert.That(subscription.transactionType, Is.EqualTo("subscription"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task My_subscription_shows_a_card_only_when_Xsolla_bills_it(CancellationToken ct = default)
    {
        var billed = await CreateSessionAsync(ct);
        var gifted = await CreateSessionAsync(ct);

        await Ultima(billed.UserId).ActivateSubscriptionAsync(UltimaTier.Monthly, 30, $"xsolla-{Guid.NewGuid():N}", null, ct);
        await Ultima(gifted.UserId).ActivateSubscriptionAsync(UltimaTier.Annual, 365, null, Guid.NewGuid(), ct);

        var billedSub = await billed.Ultima.GetMySubscription(ct);
        var giftedSub = await gifted.Ultima.GetMySubscription(ct);

        Assert.Multiple(() =>
        {
            Assert.That(billedSub!.paymentAccount, Is.Not.Null);
            Assert.That(billedSub.paymentAccount!.cardLastFour, Is.EqualTo("4242"));

            Assert.That(giftedSub!.tier, Is.EqualTo(UltimaPlan.Annual));
            Assert.That(giftedSub.autoRenew, Is.False);
            Assert.That(giftedSub.paymentAccount, Is.Null, "nothing bills a gifted subscription, so there is no card to show");
        });

        Assert.That(await Ultima(Guid.NewGuid()).GetXsollaSubscriptionIdAsync(ct), Is.Null);
    }

    // ── Boosts ───────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_boost_that_is_missing_unapplied_or_bound_for_a_foreign_space_is_not_moved(CancellationToken ct = default)
    {
        var booster  = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);

        var home    = ((SuccessCreateSpace)await booster.Users.CreateSpace(new CreateServerRequest("Home", "", ""), ct)).space.spaceId;
        var foreign = ((SuccessCreateSpace)await stranger.Users.CreateSpace(new CreateServerRequest("Foreign", "", ""), ct)).space.spaceId;

        await Ultima(booster.UserId).GrantPurchasedBoostsAsync(2, BoostSource.PurchasedPack3, $"tx-{Guid.NewGuid():N}", 30, ct);

        var boosts  = (await booster.Ultima.GetMyBoosts(ct)).Values;
        var applied = boosts[0].boostId;
        var idle    = boosts[1].boostId;

        Assert.That(await booster.Ultima.ApplyBoost(applied, home, ct), Is.InstanceOf<SuccessApplyBoost>());

        var missing   = await booster.Ultima.TransferBoost(Guid.NewGuid(), home, ct);
        var unapplied = await booster.Ultima.TransferBoost(idle, home, ct);
        var outsider  = await booster.Ultima.TransferBoost(applied, foreign, ct);

        Assert.Multiple(() =>
        {
            Assert.That((missing as FailedTransfer)?.error, Is.EqualTo(TransferBoostError.NOT_FOUND));
            Assert.That((unapplied as FailedTransfer)?.error, Is.EqualTo(TransferBoostError.NOT_APPLIED));
            Assert.That((outsider as FailedTransfer)?.error, Is.EqualTo(TransferBoostError.NOT_A_MEMBER));
        });

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await booster.Ultima.RemoveBoost(Guid.NewGuid(), ct), Is.False);

            // Taking off a boost that is on nothing is allowed and leaves it on nothing.
            Assert.That(await booster.Ultima.RemoveBoost(idle, ct), Is.True);
            Assert.That((await booster.Ultima.GetMyBoosts(ct)).Values.Single(b => b.boostId == idle).spaceId, Is.Null);

            Assert.That((await booster.Ultima.GetSpaceBoostStatus(home, ct)).boostCount, Is.EqualTo(1), "a refused transfer moved the boost anyway");
            Assert.That((await booster.Ultima.GetSpaceBoostStatus(foreign, ct)).boostCount, Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task When_a_subscription_expires_its_boosts_leave_the_spaces_they_were_in(CancellationToken ct = default)
    {
        var booster = await CreateSessionAsync(ct);
        var spaceId = ((SuccessCreateSpace)await booster.Users.CreateSpace(new CreateServerRequest("Boosted", "", ""), ct)).space.spaceId;

        await Ultima(booster.UserId).ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

        foreach (var boost in (await booster.Ultima.GetMyBoosts(ct)).Values)
            Assert.That(await booster.Ultima.ApplyBoost(boost.boostId, spaceId, ct), Is.InstanceOf<SuccessApplyBoost>());

        Assert.That((await booster.Ultima.GetSpaceBoostStatus(spaceId, ct)).boostCount, Is.EqualTo(2));
        Assert.That((await booster.Ultima.GetMySubscription(ct))!.usedBoostSlots, Is.EqualTo(2));

        await Ultima(booster.UserId).ExpireSubscriptionAsync(ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await booster.Ultima.GetSpaceBoostStatus(spaceId, ct)).boostCount, Is.Zero,
                "the space kept boosts from a subscription that is gone");
            Assert.That(await booster.Ultima.GetMyBoosts(ct), Is.Empty);
            Assert.That(await booster.Ultima.GetMySubscription(ct), Is.Null);
        });
    }

    // ── Renewal, cancellation, refund ────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Renewing_a_subscription_in_its_grace_period_counts_from_now(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var grain      = Ultima(subscriber.UserId);
        var firstSubId = $"xsolla-{Guid.NewGuid():N}";

        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, firstSubId, null, ct);

        // Billing failed and the paid period ran out two days ago: the subscription is hanging on.
        await using (var db = await DbAsync(ct))
        {
            await db.UltimaSubscriptions
               .Where(s => s.UserId == subscriber.UserId)
               .ExecuteUpdateAsync(s => s
                   .SetProperty(x => x.Status, UltimaStatus.GracePeriod)
                   .SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-2)), ct);
        }

        Assert.That((await grain.GetSubscriptionAsync(ct))!.status, Is.EqualTo(UltimaSubscriptionStatus.GracePeriod));

        var renewedSubId = $"xsolla-{Guid.NewGuid():N}";
        await grain.ActivateSubscriptionAsync(UltimaTier.Annual, 365, renewedSubId, null, ct);

        var renewed = await grain.GetSubscriptionAsync(ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(renewed!.status, Is.EqualTo(UltimaSubscriptionStatus.Active));
            Assert.That(renewed.tier, Is.EqualTo(UltimaPlan.Annual));

            // From now, not from the lapsed date: the days in arrears are not charged twice.
            Assert.That(renewed.expiresAt, Is.EqualTo(DateTimeOffset.UtcNow.AddDays(365)).Within(TimeSpan.FromMinutes(1)));
            Assert.That(await grain.GetXsollaSubscriptionIdAsync(ct), Is.EqualTo(renewedSubId));
            Assert.That(await grain.GetBoostsAsync(ct), Has.Count.EqualTo(2), "a renewal added boost slots of its own");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_failed_Xsolla_sync_does_not_undo_the_subscription(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var grain      = Ultima(subscriber.UserId);

        GetFakeXsolla().FailAttributeUpdatesFor[subscriber.UserId] = true;

        try
        {
            await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

            Assert.That(await HasActiveUltimaAsync(subscriber.UserId, ct), Is.True);
            Assert.That((await grain.GetSubscriptionAsync(ct))!.status, Is.EqualTo(UltimaSubscriptionStatus.Active));

            await grain.ExpireSubscriptionAsync(ct);

            Assert.That(await HasActiveUltimaAsync(subscriber.UserId, ct), Is.False);
            Assert.That(await grain.GetSubscriptionAsync(ct), Is.Null);
        }
        finally
        {
            GetFakeXsolla().FailAttributeUpdatesFor.TryRemove(subscriber.UserId, out _);
        }
    }

    [Test, CancelAfter(120_000)]
    public async Task Cancelling_or_refunding_with_nothing_to_end_changes_nothing(CancellationToken ct = default)
    {
        var nobody = await CreateSessionAsync(ct);

        Assert.That(await nobody.Ultima.CancelSubscription(ct), Is.False);

        // A refund for somebody with no subscription — a boost pack, say — has nothing to expire.
        await Ultima(nobody.UserId).ExpireSubscriptionAsync(ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await HasActiveUltimaAsync(nobody.UserId, ct), Is.False);
            Assert.That(await SubscriptionsAsync(nobody.UserId, ct), Is.Empty);
        });
    }

    /// <summary>
    /// Cancelling only switches the renewal off; the period stays paid for and the subscription stays
    /// <c>Cancelled</c> until it ends. <c>ExpireSubscriptionAsync</c> looked only for <c>Active</c> and
    /// <c>GracePeriod</c>, so a refund of a cancelled subscription — the usual order: switch renewal
    /// off, then ask for the money back — found nothing and left Ultima, its boosts and the premium
    /// flag in place for good. The operator console's Expire button had the same blind spot.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_refund_after_cancelling_still_takes_Ultima_away(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var grain      = Ultima(subscriber.UserId);

        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, $"xsolla-{Guid.NewGuid():N}", null, ct);

        Assert.That(await subscriber.Ultima.CancelSubscription(ct), Is.True);
        Assert.That((await grain.GetSubscriptionAsync(ct))!.status, Is.EqualTo(UltimaSubscriptionStatus.Cancelled));
        Assert.That(await HasActiveUltimaAsync(subscriber.UserId, ct), Is.True, "cancelling is not supposed to end the paid period");

        var status = await PostWebhookAsync(new
        {
            notification_type = "refund",
            transaction       = new { id = Random.Shared.NextInt64(1_000_000, 9_000_000) },
            custom_parameters = new { user_id = subscriber.UserId.ToString() }
        }, ct);

        Assert.That(status, Is.EqualTo(HttpStatusCode.NoContent));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await HasActiveUltimaAsync(subscriber.UserId, ct), Is.False, "a refunded subscription kept its premium flag");
            Assert.That(await grain.GetSubscriptionAsync(ct), Is.Null);
            Assert.That(await grain.GetBoostsAsync(ct), Is.Empty);
            Assert.That((await SubscriptionsAsync(subscriber.UserId, ct)).Select(s => s.Status), Is.EqualTo(new[] { UltimaStatus.Expired }));
        });
    }

    /// <summary>
    /// Subscribing again while a cancelled subscription is still running used to open a second
    /// subscription beside it, with two boost slots of its own — and the cancelled one kept its two,
    /// which never expire by themselves. Every cancel-and-resubscribe was two free boosts.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Subscribing_again_after_cancelling_extends_the_one_subscription(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var grain      = Ultima(subscriber.UserId);

        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, $"xsolla-{Guid.NewGuid():N}", null, ct);
        var paidUntil = (await grain.GetSubscriptionAsync(ct))!.expiresAt;

        Assert.That(await subscriber.Ultima.CancelSubscription(ct), Is.True);

        var newSubId = $"xsolla-{Guid.NewGuid():N}";
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, newSubId, null, ct);

        var current = await grain.GetSubscriptionAsync(ct);
        var rows    = await SubscriptionsAsync(subscriber.UserId, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(rows, Has.Count.EqualTo(1), "a second subscription was opened beside the cancelled one");
            Assert.That(await grain.GetBoostsAsync(ct), Has.Count.EqualTo(2), "resubscribing handed out another set of boost slots");

            Assert.That(current!.status, Is.EqualTo(UltimaSubscriptionStatus.Active));
            Assert.That(current.autoRenew, Is.True, "the new Xsolla subscription renews");
            Assert.That(current.expiresAt, Is.EqualTo(paidUntil.AddDays(30)).Within(TimeSpan.FromSeconds(5)),
                "the days already paid for were lost");
            Assert.That(await grain.GetXsollaSubscriptionIdAsync(ct), Is.EqualTo(newSubId));
        });
    }

    // ── Running out ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing used to end a subscription when its time ran out: <c>ExpireSubscriptionAsync</c> ran only
    /// on a refund, a cancelled order or the console's button. A subscription Xsolla bills at least
    /// hears when billing stops, but a gifted one and one granted from the console have no Xsolla
    /// subscription at all — a month of Ultima given as a gift was Ultima for ever, premium flag,
    /// subscription boosts and everything worn on them included.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_subscription_ends_when_its_time_runs_out(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var grain      = Ultima(subscriber.UserId);

        // A gift of no days at all: its time is up the moment it starts.
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 0, null, Guid.NewGuid(), ct);

        var ended = await Poll.UntilAsync(async () => !await HasActiveUltimaAsync(subscriber.UserId, ct),
            ReminderDeadline, TimeSpan.FromMilliseconds(250), ct);

        Assert.That(ended, Is.True, "a subscription whose time ran out still makes its holder an Ultima subscriber");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await grain.GetSubscriptionAsync(ct), Is.Null);
            Assert.That(await grain.GetBoostsAsync(ct), Is.Empty, "the subscription's boost slots outlived it");
            Assert.That((await SubscriptionsAsync(subscriber.UserId, ct)).Select(s => s.Status), Is.EqualTo(new[] { UltimaStatus.Expired }));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_cancelled_subscription_ends_when_its_paid_period_does_with_no_grace(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);

        await Ultima(subscriber.UserId).ActivateSubscriptionAsync(UltimaTier.Monthly, 30, $"xsolla-{Guid.NewGuid():N}", null, ct);

        // A few seconds of the paid period left when the renewal is switched off.
        var paidUntil = DateTimeOffset.UtcNow.AddSeconds(4);
        await EndsAtAsync(subscriber.UserId, paidUntil, ct);

        Assert.That(await subscriber.Ultima.CancelSubscription(ct), Is.True);
        Assert.That(await HasActiveUltimaAsync(subscriber.UserId, ct), Is.True, "cancelling ended the period that was paid for");

        var ended = await Poll.UntilAsync(async () => !await HasActiveUltimaAsync(subscriber.UserId, ct),
            ReminderDeadline, TimeSpan.FromMilliseconds(250), ct);

        Assert.Multiple(() =>
        {
            // The shipped grace is three days, so ending at all within the deadline means none was given.
            Assert.That(ended, Is.True, "a cancelled subscription outlived its paid period");
            Assert.That(DateTimeOffset.UtcNow, Is.GreaterThanOrEqualTo(paidUntil), "it ended before the time that was paid for");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_subscription_Xsolla_renews_waits_out_its_grace_before_it_ends(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var grain      = Ultima(subscriber.UserId);
        var grace      = TimeSpan.FromSeconds(5);

        await WithRenewalGraceAsync(grace, async () =>
        {
            // The paid period is over the moment it starts, and the renewal is late.
            await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 0, $"xsolla-{Guid.NewGuid():N}", null, ct);
            var paidUntil = (await grain.GetSubscriptionAsync(ct))!.expiresAt;

            var inGrace = await Poll.ForValueAsync(() => StatusAsync(subscriber.UserId, ct),
                status => status is UltimaSubscriptionStatus.GracePeriod, ReminderDeadline, TimeSpan.FromMilliseconds(250), ct);

            Assert.That(inGrace, Is.EqualTo(UltimaSubscriptionStatus.GracePeriod));
            Assert.That(await HasActiveUltimaAsync(subscriber.UserId, ct), Is.True, "Ultima was taken away inside the grace");

            var ended = await Poll.UntilAsync(async () => !await HasActiveUltimaAsync(subscriber.UserId, ct),
                ReminderDeadline, TimeSpan.FromMilliseconds(250), ct);

            Assert.Multiple(() =>
            {
                Assert.That(ended, Is.True, "a renewal that never came left the subscription running past its grace");
                Assert.That(DateTimeOffset.UtcNow, Is.GreaterThanOrEqualTo(paidUntil + grace), "it ended before the grace was out");
            });
        });

        Assert.That(await grain.GetBoostsAsync(ct), Is.Empty);
    }

    [Test, CancelAfter(120_000)]
    public async Task A_renewal_that_arrives_inside_the_grace_keeps_the_subscription(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var grain      = Ultima(subscriber.UserId);
        var subId      = $"xsolla-{Guid.NewGuid():N}";
        var grace      = TimeSpan.FromSeconds(4);

        await WithRenewalGraceAsync(grace, async () =>
        {
            await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 0, subId, null, ct);

            Assert.That(await Poll.ForValueAsync(() => StatusAsync(subscriber.UserId, ct),
                status => status is UltimaSubscriptionStatus.GracePeriod, ReminderDeadline, TimeSpan.FromMilliseconds(250), ct),
                Is.EqualTo(UltimaSubscriptionStatus.GracePeriod));

            // Xsolla's renewal, late but inside the grace.
            await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, subId, null, ct);

            await Task.Delay(grace + TimeSpan.FromSeconds(3), ct);

            var renewed = await grain.GetSubscriptionAsync(ct);

            await Assert.MultipleAsync(async () =>
            {
                Assert.That(renewed!.status, Is.EqualTo(UltimaSubscriptionStatus.Active));
                Assert.That(renewed.expiresAt, Is.EqualTo(DateTimeOffset.UtcNow.AddDays(30)).Within(TimeSpan.FromMinutes(1)));
                Assert.That(await HasActiveUltimaAsync(subscriber.UserId, ct), Is.True, "the renewed subscription ended at the old grace");
                Assert.That(await grain.GetBoostsAsync(ct), Has.Count.EqualTo(2));
            });
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Cancelling_inside_the_grace_ends_the_subscription_at_once(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var grain      = Ultima(subscriber.UserId);

        // Long enough that only the cancellation can be what ends it.
        await WithRenewalGraceAsync(TimeSpan.FromMinutes(10), async () =>
        {
            await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 0, $"xsolla-{Guid.NewGuid():N}", null, ct);

            Assert.That(await Poll.ForValueAsync(() => StatusAsync(subscriber.UserId, ct),
                status => status is UltimaSubscriptionStatus.GracePeriod, ReminderDeadline, TimeSpan.FromMilliseconds(250), ct),
                Is.EqualTo(UltimaSubscriptionStatus.GracePeriod));

            // Xsolla giving up on the renewal, or the subscriber switching it off.
            Assert.That(await subscriber.Ultima.CancelSubscription(ct), Is.True);

            var ended = await Poll.UntilAsync(async () => !await HasActiveUltimaAsync(subscriber.UserId, ct),
                ReminderDeadline, TimeSpan.FromMilliseconds(250), ct);

            Assert.That(ended, Is.True, "a subscription cancelled after its paid period kept running");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Subscribing_again_before_a_cancelled_subscription_ends_keeps_it_going(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var grain      = Ultima(subscriber.UserId);

        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, $"xsolla-{Guid.NewGuid():N}", null, ct);

        var paidUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        await EndsAtAsync(subscriber.UserId, paidUntil, ct);

        Assert.That(await subscriber.Ultima.CancelSubscription(ct), Is.True);

        // Changed their mind before the end: the new period runs on from the old one.
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, $"xsolla-{Guid.NewGuid():N}", null, ct);

        await Task.Delay(paidUntil - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4), ct);

        var current = await grain.GetSubscriptionAsync(ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await HasActiveUltimaAsync(subscriber.UserId, ct), Is.True, "the subscription ended at the cancelled period's end");
            Assert.That(current!.status, Is.EqualTo(UltimaSubscriptionStatus.Active));
            Assert.That(current.expiresAt, Is.EqualTo(paidUntil.AddDays(30)).Within(TimeSpan.FromSeconds(2)));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_subscription_from_before_expiry_was_scheduled_ends_once_its_grain_wakes(CancellationToken ct = default)
    {
        var subscriber = await CreateSessionAsync(ct);
        var now        = DateTimeOffset.UtcNow;

        // Written straight to the database, as every subscription before this release was: no reminder.
        await using (var db = await DbAsync(ct))
        {
            db.UltimaSubscriptions.Add(new UltimaSubscriptionEntity
            {
                Id         = Guid.NewGuid(),
                UserId     = subscriber.UserId,
                Tier       = UltimaTier.Monthly,
                Status     = UltimaStatus.Active,
                StartsAt   = now.AddDays(-31),
                ExpiresAt  = now.AddDays(-1),
                BoostSlots = 2,
                CreatedAt  = now.AddDays(-31)
            });

            await db.SaveChangesAsync(ct);

            await db.Users.Where(u => u.Id == subscriber.UserId)
               .ExecuteUpdateAsync(s => s.SetProperty(u => u.HasActiveUltima, true), ct);
        }

        // Anything that wakes the grain — here, the subscriber opening the Ultima screen.
        await subscriber.Ultima.GetMySubscription(ct);

        var ended = await Poll.UntilAsync(async () => !await HasActiveUltimaAsync(subscriber.UserId, ct),
            ReminderDeadline, TimeSpan.FromMilliseconds(250), ct);

        Assert.That(ended, Is.True, "a subscription that ran out before the reminder existed was never ended");
    }
}
