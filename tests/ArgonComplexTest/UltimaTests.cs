namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[TestFixture, Parallelizable(ParallelScope.None)]
public class UltimaTests : TestBase
{
    [SetUp]
    public void ResetFakeXsolla() => GetFakeXsolla().Reset();

    #region Pricing

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task GetPricing_ReturnsPricesFromXsolla(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var result = await GetUltimaService(scope.ServiceProvider).GetPricing(ct);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.subscriptionMonthly.amount, Is.EqualTo("9.99"));
        Assert.That(result.subscriptionAnnual.amount, Is.EqualTo("99.99"));
        Assert.That(result.boostPack1.amount, Is.EqualTo("4.99"));
        Assert.That(result.boostPack3.amount, Is.EqualTo("12.99"));
        Assert.That(result.boostPack5.amount, Is.EqualTo("19.99"));
        Assert.That(result.boostPack1.currency, Is.EqualTo("USD"));
    }

    #endregion

    #region Subscription Lifecycle

    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task GetSubscription_NoSub_ReturnsNull(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var result = await GetUltimaService(scope.ServiceProvider).GetMySubscription(ct);

        Assert.That(result, Is.Null);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task ActivateSubscription_NewUser_CreatesSubAndBoosts(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var userId = user.userId;

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, "xsolla_sub_123", null, ct);

        // Verify subscription created
        var sub = await grain.GetSubscriptionAsync(ct);
        Assert.That(sub, Is.Not.Null);
        Assert.That(sub!.tier, Is.EqualTo(UltimaPlan.Monthly));
        Assert.That(sub.status, Is.EqualTo(UltimaSubscriptionStatus.Active));
        Assert.That(sub.totalBoostSlots, Is.EqualTo(3));
        Assert.That(sub.usedBoostSlots, Is.EqualTo(0));
        Assert.That(sub.autoRenew, Is.True);

        // Verify 3 boosts created
        var boosts = await grain.GetBoostsAsync(ct);
        Assert.That(boosts, Has.Count.EqualTo(3));
        Assert.That(boosts.All(b => b.source == BoostSource.Subscription), Is.True);
        Assert.That(boosts.All(b => b.spaceId == null), Is.True);

        // Verify HasActiveUltima flag
        var ctx = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await ctx.CreateDbContextAsync(ct);
        var userEntity = await db.Users.AsNoTracking().FirstAsync(x => x.Id == userId, ct);
        Assert.That(userEntity.HasActiveUltima, Is.True);

        // Verify Xsolla attribute sync
        var fakeXsolla = GetFakeXsolla();
        Assert.That(fakeXsolla.AttributeUpdates.Any(a => a.UserId == userId && a.Key == "ultima_subscriber" && a.Value == "1"), Is.True);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(2)]
    public async Task ActivateSubscription_ExistingActive_ExtendsExpiry(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);

        // First activation: 30 days
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, "sub_1", null, ct);
        var sub1 = await grain.GetSubscriptionAsync(ct);
        var firstExpiry = sub1!.expiresAt;

        // Second activation: extend by 30 days
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);
        var sub2 = await grain.GetSubscriptionAsync(ct);

        Assert.That(sub2!.expiresAt, Is.GreaterThan(firstExpiry));
        // Should be ~60 days from start (with tolerance for test execution time)
        var expectedExpiry = firstExpiry.AddDays(30);
        Assert.That(sub2.expiresAt, Is.EqualTo(expectedExpiry).Within(TimeSpan.FromSeconds(5)));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(3)]
    public async Task CancelSubscription_SetsStatusAndAutoRenew(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, "sub_cancel_test", null, ct);

        var result = await grain.CancelSubscriptionAsync(ct);
        Assert.That(result, Is.True);

        var sub = await grain.GetSubscriptionAsync(ct);
        Assert.That(sub, Is.Not.Null);
        Assert.That(sub!.status, Is.EqualTo(UltimaSubscriptionStatus.Cancelled));
        Assert.That(sub.autoRenew, Is.False);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(4)]
    public async Task ExpireSubscription_RemovesSubBoosts_KeepsPurchased(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var userId = user.userId;

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(userId);

        // Activate subscription → 2 sub boosts
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, "sub_expire_test", null, ct);

        // Grant 2 purchased boosts
        await grain.GrantPurchasedBoostsAsync(2, BoostSource.PurchasedPack3, "tx_test", ct: ct);

        var boostsBefore = await grain.GetBoostsAsync(ct);
        Assert.That(boostsBefore, Has.Count.EqualTo(4)); // 2 sub + 2 purchased

        // Expire
        await grain.ExpireSubscriptionAsync(ct);

        // Only purchased boosts survive
        var boostsAfter = await grain.GetBoostsAsync(ct);
        Assert.That(boostsAfter, Has.Count.EqualTo(2));
        Assert.That(boostsAfter.All(b => b.source == BoostSource.PurchasedPack3), Is.True);

        // HasActiveUltima cleared
        var ctx = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await ctx.CreateDbContextAsync(ct);
        var userEntity = await db.Users.AsNoTracking().FirstAsync(x => x.Id == userId, ct);
        Assert.That(userEntity.HasActiveUltima, Is.False);

        // Xsolla attribute cleared
        var fakeXsolla = GetFakeXsolla();
        Assert.That(fakeXsolla.AttributeUpdates.Any(a => a.UserId == userId && a.Key == "ultima_subscriber" && a.Value == "0"), Is.True);
    }

    #endregion

    #region Boost Management

    [Test, CancelAfter(1000 * 60 * 5), Order(10)]
    public async Task ApplyBoost_ToMemberSpace_Succeeds(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var userId = user.userId;

        // Create space (user auto-joins)
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        // Activate sub → 3 boosts
        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

        var boosts = await grain.GetBoostsAsync(ct);
        var boostId = boosts.First().boostId;

        // Apply boost to space
        var result = await grain.ApplyBoostAsync(boostId, spaceId, ct);
        Assert.That(result, Is.InstanceOf<SuccessApplyBoost>());

        // Verify boost has SpaceId
        var boostsAfter = await grain.GetBoostsAsync(ct);
        var applied = boostsAfter.First(b => b.boostId == boostId);
        Assert.That(applied.spaceId, Is.EqualTo(spaceId));
        Assert.That(applied.appliedAt, Is.Not.Null);

        // Verify space boost status
        var spaceBoostGrain = GetGrainFactory().GetGrain<ISpaceBoostGrain>(spaceId);
        var status = await spaceBoostGrain.GetBoostStatusAsync(ct);
        Assert.That(status.boostCount, Is.EqualTo(1));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(11)]
    public async Task ApplyBoost_AlreadyApplied_ReturnsError(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

        var boosts = await grain.GetBoostsAsync(ct);
        var boostId = boosts.First().boostId;

        // Apply once
        await grain.ApplyBoostAsync(boostId, spaceId, ct);

        // Apply same boost again
        var result = await grain.ApplyBoostAsync(boostId, spaceId, ct);
        Assert.That(result, Is.InstanceOf<FailedApplyBoost>());
        Assert.That(((FailedApplyBoost)result).error, Is.EqualTo(ApplyBoostError.ALREADY_APPLIED));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(12)]
    public async Task ApplyBoost_NotMember_ReturnsError(CancellationToken ct = default)
    {
        // Register user1, activate sub
        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user1 = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user1.userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

        var boosts = await grain.GetBoostsAsync(ct);
        var boostId = boosts.First().boostId;

        // Register user2, create space (user2 is member, user1 is not)
        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        // User1 tries to apply boost to user2's space
        var result = await grain.ApplyBoostAsync(boostId, spaceId, ct);
        Assert.That(result, Is.InstanceOf<FailedApplyBoost>());
        Assert.That(((FailedApplyBoost)result).error, Is.EqualTo(ApplyBoostError.NOT_A_MEMBER));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(13)]
    public async Task ApplyBoost_NotFound_ReturnsError(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        var result = await grain.ApplyBoostAsync(Guid.NewGuid(), spaceId, ct);

        Assert.That(result, Is.InstanceOf<FailedApplyBoost>());
        Assert.That(((FailedApplyBoost)result).error, Is.EqualTo(ApplyBoostError.NOT_FOUND));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(14)]
    public async Task TransferBoost_ToNewSpace_SetsNewSpaceAndCooldown(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var space1 = await CreateSpaceAndGetIdAsync(ct);
        var space2 = await CreateSpaceAndGetIdAsync(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

        var boosts = await grain.GetBoostsAsync(ct);
        var boostId = boosts.First().boostId;

        // Apply to space1
        await grain.ApplyBoostAsync(boostId, space1, ct);

        // Transfer to space2
        var result = await grain.TransferBoostAsync(boostId, space2, ct);
        Assert.That(result, Is.InstanceOf<SuccessTransfer>());

        var boostsAfter = await grain.GetBoostsAsync(ct);
        var transferred = boostsAfter.First(b => b.boostId == boostId);
        Assert.That(transferred.spaceId, Is.EqualTo(space2));
        Assert.That(transferred.transferCooldownUntil, Is.Not.Null);
        Assert.That(transferred.transferCooldownUntil, Is.GreaterThan(DateTime.UtcNow));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(15)]
    public async Task TransferBoost_OnCooldown_ReturnsError(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var space1 = await CreateSpaceAndGetIdAsync(ct);
        var space2 = await CreateSpaceAndGetIdAsync(ct);
        var space3 = await CreateSpaceAndGetIdAsync(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

        var boosts = await grain.GetBoostsAsync(ct);
        var boostId = boosts.First().boostId;

        await grain.ApplyBoostAsync(boostId, space1, ct);
        await grain.TransferBoostAsync(boostId, space2, ct);

        // Try transfer again immediately → cooldown
        var result = await grain.TransferBoostAsync(boostId, space3, ct);
        Assert.That(result, Is.InstanceOf<FailedTransfer>());
        Assert.That(((FailedTransfer)result).error, Is.EqualTo(TransferBoostError.ON_COOLDOWN));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(16)]
    public async Task RemoveBoost_Applied_ClearsSpaceId(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

        var boosts = await grain.GetBoostsAsync(ct);
        var boostId = boosts.First().boostId;

        await grain.ApplyBoostAsync(boostId, spaceId, ct);
        var removed = await grain.RemoveBoostAsync(boostId, ct);
        Assert.That(removed, Is.True);

        var boostsAfter = await grain.GetBoostsAsync(ct);
        var removedBoost = boostsAfter.First(b => b.boostId == boostId);
        Assert.That(removedBoost.spaceId, Is.Null);
        Assert.That(removedBoost.appliedAt, Is.Null);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(17)]
    public async Task GrantPurchasedBoosts_CreatesCorrectCount(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.GrantPurchasedBoostsAsync(5, BoostSource.PurchasedPack5, "tx_pack5", ct: ct);

        var boosts = await grain.GetBoostsAsync(ct);
        Assert.That(boosts, Has.Count.EqualTo(5));
        Assert.That(boosts.All(b => b.source == BoostSource.PurchasedPack5), Is.True);
    }

    #endregion

    #region Space Boost Levels

    [Test, CancelAfter(1000 * 60 * 5), Order(20)]
    public async Task SpaceBoostLevel_Under3_Level0(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.GrantPurchasedBoostsAsync(2, BoostSource.PurchasedPack3, "tx_lvl0", ct: ct);

        var boosts = await grain.GetBoostsAsync(ct);
        foreach (var b in boosts)
            await grain.ApplyBoostAsync(b.boostId, spaceId, ct);

        var status = await GetGrainFactory().GetGrain<ISpaceBoostGrain>(spaceId).GetBoostStatusAsync(ct);
        Assert.That(status.boostCount, Is.EqualTo(2));
        Assert.That(status.boostLevel, Is.EqualTo(0));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(21)]
    public async Task SpaceBoostLevel_3Boosts_Level1(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.GrantPurchasedBoostsAsync(3, BoostSource.PurchasedPack3, "tx_lvl1", ct: ct);

        var boosts = await grain.GetBoostsAsync(ct);
        foreach (var b in boosts)
            await grain.ApplyBoostAsync(b.boostId, spaceId, ct);

        var status = await GetGrainFactory().GetGrain<ISpaceBoostGrain>(spaceId).GetBoostStatusAsync(ct);
        Assert.That(status.boostCount, Is.EqualTo(3));
        Assert.That(status.boostLevel, Is.EqualTo(1));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(22)]
    public async Task SpaceBoostLevel_7Boosts_Level2(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.GrantPurchasedBoostsAsync(7, BoostSource.PurchasedPack5, "tx_lvl2", ct: ct);

        var boosts = await grain.GetBoostsAsync(ct);
        foreach (var b in boosts)
            await grain.ApplyBoostAsync(b.boostId, spaceId, ct);

        var status = await GetGrainFactory().GetGrain<ISpaceBoostGrain>(spaceId).GetBoostStatusAsync(ct);
        Assert.That(status.boostCount, Is.EqualTo(7));
        Assert.That(status.boostLevel, Is.EqualTo(2));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(23)]
    public async Task SpaceBoostLevel_14Boosts_Level3(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.GrantPurchasedBoostsAsync(14, BoostSource.PurchasedPack5, "tx_lvl3", ct: ct);

        var boosts = await grain.GetBoostsAsync(ct);
        foreach (var b in boosts)
            await grain.ApplyBoostAsync(b.boostId, spaceId, ct);

        var status = await GetGrainFactory().GetGrain<ISpaceBoostGrain>(spaceId).GetBoostStatusAsync(ct);
        Assert.That(status.boostCount, Is.EqualTo(14));
        Assert.That(status.boostLevel, Is.EqualTo(3));
    }

    #endregion

    #region Ion Service Layer

    [Test, CancelAfter(1000 * 60 * 5), Order(30)]
    public async Task Ion_GetMySubscription_NoSub_ReturnsNull(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var result = await GetUltimaService(scope.ServiceProvider).GetMySubscription(ct);
        Assert.That(result, Is.Null);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(31)]
    public async Task Ion_GetMyBoosts_AfterActivation_Returns3Boosts(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        // Activate via grain directly
        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Annual, 365, null, null, ct);

        var boosts = await GetUltimaService(scope.ServiceProvider).GetMyBoosts(ct);
        Assert.That(boosts.Values, Has.Count.EqualTo(3));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(32)]
    public async Task Ion_PurchaseBoostPack_ReturnsCheckoutUrl(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        // Purchase boost pack via Ion
        var result = await GetUltimaService(scope.ServiceProvider).PurchaseBoostPack(BoostPackType.Pack3, ct);
        Assert.That(result, Is.InstanceOf<SuccessPurchaseBoost>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(33)]
    public async Task Ion_SendUltimaGift_ToSelf_ReturnsSelfGiftError(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var result = await GetUltimaService(scope.ServiceProvider).SendUltimaGift(user.userId, UltimaPlan.Monthly, "hi", ct);
        Assert.That(result, Is.InstanceOf<FailedSendGift>());
        Assert.That(((FailedSendGift)result).error, Is.EqualTo(SendGiftError.SELF_GIFT));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(34)]
    public async Task Ion_SendUltimaGift_ValidRecipient_ReturnsCheckoutUrl(CancellationToken ct = default)
    {
        // Register sender
        var senderToken = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(senderToken);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        // Register recipient
        var recipientToken = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(recipientToken);
        var recipient = await GetUserService(scope.ServiceProvider).GetMe(ct);

        // Switch back to sender
        SetAuthToken(senderToken);

        var result = await GetUltimaService(scope.ServiceProvider).SendUltimaGift(recipient.userId, UltimaPlan.Monthly, "enjoy!", ct);
        Assert.That(result, Is.InstanceOf<SuccessSendGift>());
        var success = (SuccessSendGift)result;
        Assert.That(success.checkoutUrl, Does.Contain("fake-checkout.test/gift"));
    }

    #endregion
}
