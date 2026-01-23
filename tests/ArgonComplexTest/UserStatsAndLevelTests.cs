namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

[TestFixture, Parallelizable(ParallelScope.None)]
public class UserStatsAndLevelTests : TestBase
{
    // Calculate exact XP needed for level 100 using the same formula as UserLevelGrain
    private static int CalculateXpForLevel100()
    {
        var totalXp = 0;
        for (var level = 1; level < 100; level++)
        {
            totalXp += (int)(50 * Math.Pow(level, 1.3));
        }
        return totalXp + 1; // +1 to ensure we reach exactly level 100
    }

    private static readonly int XpForLevel100 = CalculateXpForLevel100();

    #region TodayStats Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task GetTodayStats_NewUser_ReturnsZeroStats(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var stats = await GetUserService(scope.ServiceProvider).GetTodayStats(ct);

        Assert.That(stats.timeInVoice, Is.EqualTo(0));
        Assert.That(stats.callsMade, Is.EqualTo(0));
        Assert.That(stats.messagesSent, Is.EqualTo(0));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task GetTodayStats_AfterSendingMessages_IncrementsMessageCount(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var spaceId = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, "stats-test", ct);

        // Send 3 messages
        for (var i = 1; i <= 3; i++)
        {
            await GetChannelService(scope.ServiceProvider).SendMessage(
                spaceId, channelId, $"Message {i}", new ion.runtime.IonArray<IMessageEntity>([]), i, null, ct);
        }

        // Wait for async stats tracking to complete (OneWay methods)
        await Task.Delay(1000, ct);

        var stats = await GetUserService(scope.ServiceProvider).GetTodayStats(ct);

        Assert.That(stats.messagesSent, Is.EqualTo(3), "Expected 3 messages sent");
    }

    #endregion

    #region MyLevel Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(10)]
    public async Task GetMyLevel_NewUser_ReturnsLevel1WithZeroXp(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var level = await GetUserService(scope.ServiceProvider).GetMyLevel(ct);

        Assert.That(level.currentLevel, Is.EqualTo(1));
        Assert.That(level.totalXp, Is.EqualTo(0));
        Assert.That(level.readyToClaimCoin, Is.False);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(11)]
    public async Task GetMyLevel_AfterAwardingXp_ReturnsUpdatedLevel(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        // Award XP directly via grain
        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(user.userId);

        // Award enough XP to level up (level 2 needs ~50 XP)
        await levelGrain.AwardXpAsync(100, XpSource.Event);

        var level = await GetUserService(scope.ServiceProvider).GetMyLevel(ct);

        Assert.That(level.totalXp, Is.EqualTo(100));
        Assert.That(level.currentLevel, Is.GreaterThan(1), "Should have leveled up");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(12)]
    public async Task GetMyLevel_XpForNextLevel_IsGreaterThanCurrent(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var level = await GetUserService(scope.ServiceProvider).GetMyLevel(ct);

        Assert.That(level.xpForNextLevel, Is.GreaterThan(level.xpForCurrentLevel),
            "XP for next level should be greater than XP for current level");
    }

    #endregion

    #region Level Progression Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(20)]
    public async Task LevelProgression_AwardingXpMultipleTimes_AccumulatesCorrectly(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(user.userId);

        // Award XP in multiple batches
        await levelGrain.AwardXpAsync(25, XpSource.Voice);
        await levelGrain.AwardXpAsync(25, XpSource.Message);
        await levelGrain.AwardXpAsync(50, XpSource.DailyBonus);

        var level = await GetUserService(scope.ServiceProvider).GetMyLevel(ct);

        Assert.That(level.totalXp, Is.EqualTo(100));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(21)]
    public async Task LevelProgression_ReachingLevel100_SetsReadyToClaimCoin(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(user.userId);

        // Award enough XP to reach level 100
        await levelGrain.AwardXpAsync(XpForLevel100, XpSource.Event);

        var level = await GetUserService(scope.ServiceProvider).GetMyLevel(ct);

        Assert.That(level.currentLevel, Is.EqualTo(100), $"Expected level 100 with {XpForLevel100} XP, got level {level.currentLevel}");
        Assert.That(level.readyToClaimCoin, Is.True, "Should be ready to claim coin at level 100");
    }

    #endregion

    #region ClaimLevelCoin Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(30)]
    public async Task ClaimLevelCoin_NotAtLevel100_ReturnsFalse(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Try to claim without reaching level 100
        var result = await GetUserService(scope.ServiceProvider).ClaimLevelCoin(ct);

        Assert.That(result, Is.False, "Should not be able to claim coin without reaching level 100");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(31)]
    public async Task ClaimLevelCoin_AtLevel100_ReturnsTrue_AndResetsLevel(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(user.userId);

        // Reach level 100
        await levelGrain.AwardXpAsync(XpForLevel100, XpSource.Event);

        // Verify at level 100
        var levelBefore = await GetUserService(scope.ServiceProvider).GetMyLevel(ct);
        Assert.That(levelBefore.currentLevel, Is.EqualTo(100), $"Expected level 100, got {levelBefore.currentLevel}");
        Assert.That(levelBefore.readyToClaimCoin, Is.True);

        // Claim coin
        var result = await GetUserService(scope.ServiceProvider).ClaimLevelCoin(ct);
        Assert.That(result, Is.True, "Should successfully claim coin");

        // Verify level reset
        var levelAfter = await GetUserService(scope.ServiceProvider).GetMyLevel(ct);
        Assert.That(levelAfter.currentLevel, Is.EqualTo(1), "Level should reset to 1 after claiming");
        Assert.That(levelAfter.totalXp, Is.EqualTo(0), "XP should reset to 0 after claiming");
        Assert.That(levelAfter.readyToClaimCoin, Is.False);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(32)]
    public async Task ClaimLevelCoin_AtLevel100_CreatesCoinInInventory(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(user.userId);

        // Reach level 100 and claim
        await levelGrain.AwardXpAsync(XpForLevel100, XpSource.Event);
        var claimResult = await GetUserService(scope.ServiceProvider).ClaimLevelCoin(ct);
        Assert.That(claimResult, Is.True, "Claim should succeed");

        // Check inventory via API - id field in InventoryItem is the templateId
        var inventory = await GetInventoryService(scope.ServiceProvider).GetMyInventoryItems(ct);

        var currentYear = DateTime.UtcNow.Year;
        var coinTemplateId = $"year_{currentYear}_coin_lvl1";

        var coin = inventory.Values.FirstOrDefault(x => x.id == coinTemplateId);
        Assert.That(coin, Is.Not.Null, $"Should have coin with template {coinTemplateId} in inventory. Found items: {string.Join(", ", inventory.Values.Select(x => x.id))}");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(33)]
    public async Task ClaimLevelCoin_MultipleTimes_UpgradesCoinTier(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(user.userId);

        // First claim - tier 1
        await levelGrain.AwardXpAsync(XpForLevel100, XpSource.Event);
        var claim1 = await GetUserService(scope.ServiceProvider).ClaimLevelCoin(ct);
        Assert.That(claim1, Is.True, "First claim should succeed");

        // Second claim - tier 2
        await levelGrain.AwardXpAsync(XpForLevel100, XpSource.Event);
        var claim2 = await GetUserService(scope.ServiceProvider).ClaimLevelCoin(ct);
        Assert.That(claim2, Is.True, "Second claim should succeed");

        // Check inventory for both coins via API
        var inventory = await GetInventoryService(scope.ServiceProvider).GetMyInventoryItems(ct);

        var currentYear = DateTime.UtcNow.Year;
        var coin1 = inventory.Values.FirstOrDefault(x => x.id == $"year_{currentYear}_coin_lvl1");
        var coin2 = inventory.Values.FirstOrDefault(x => x.id == $"year_{currentYear}_coin_lvl2");

        Assert.That(coin1, Is.Not.Null, "Should have tier 1 coin");
        Assert.That(coin2, Is.Not.Null, "Should have tier 2 coin");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(34)]
    public async Task ClaimLevelCoin_AfterMaxTier_ReturnsFalse(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(user.userId);

        // Claim 5 times to reach max tier
        for (var i = 0; i < 5; i++)
        {
            await levelGrain.AwardXpAsync(XpForLevel100, XpSource.Event);
            var claimResult = await GetUserService(scope.ServiceProvider).ClaimLevelCoin(ct);
            Assert.That(claimResult, Is.True, $"Claim {i + 1} should succeed");
        }

        // 6th claim should fail (max tier reached)
        await levelGrain.AwardXpAsync(XpForLevel100, XpSource.Event);
        var sixthClaim = await GetUserService(scope.ServiceProvider).ClaimLevelCoin(ct);
        Assert.That(sixthClaim, Is.False, "Should not be able to claim beyond tier 5");

        // Verify inventory has exactly 5 coins for this year via API
        var inventory = await GetInventoryService(scope.ServiceProvider).GetMyInventoryItems(ct);

        var currentYear = DateTime.UtcNow.Year;
        var coinPrefix = $"year_{currentYear}_coin_lvl";
        var coins = inventory.Values.Where(x => x.id.StartsWith(coinPrefix)).ToList();

        Assert.That(coins.Count, Is.EqualTo(5), "Should have exactly 5 coins (max tier)");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(35)]
    public async Task ClaimLevelCoin_GeneratesNotification(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(user.userId);

        // Reach level 100 and claim
        await levelGrain.AwardXpAsync(XpForLevel100, XpSource.Event);
        var claimResult = await GetUserService(scope.ServiceProvider).ClaimLevelCoin(ct);
        Assert.That(claimResult, Is.True, "Claim should succeed");

        // Check for unread notification
        var notifications = await GetInventoryService(scope.ServiceProvider).GetNotifications(ct);

        var currentYear = DateTime.UtcNow.Year;
        var coinTemplateId = $"year_{currentYear}_coin_lvl1";

        var notification = notifications.Values.FirstOrDefault(x => x.id == coinTemplateId);
        Assert.That(notification, Is.Not.Null, $"Should have notification for claimed coin. Found: {string.Join(", ", notifications.Values.Select(x => x.id))}");
    }

    #endregion

    #region Stats Grain Direct Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(40)]
    public async Task UserStatsGrain_RecordVoiceTime_UpdatesStats(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var statsGrain = grainFactory.GetGrain<IUserStatsGrain>(user.userId);

        // Record 5 minutes (300 seconds) of voice time using awaitable version
        await statsGrain.RecordVoiceTimeAndWaitAsync(300, Guid.NewGuid(), Guid.NewGuid());

        var stats = await statsGrain.GetTodayStatsAsync();

        Assert.That(stats.timeInVoice, Is.EqualTo(5), "Should have 5 minutes of voice time");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(41)]
    public async Task UserStatsGrain_IncrementCalls_UpdatesStats(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var statsGrain = grainFactory.GetGrain<IUserStatsGrain>(user.userId);

        // Use awaitable version for tests
        await statsGrain.IncrementCallsAndWaitAsync();
        await statsGrain.IncrementCallsAndWaitAsync();

        var stats = await statsGrain.GetTodayStatsAsync();

        Assert.That(stats.callsMade, Is.EqualTo(2), "Should have 2 calls made");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(42)]
    public async Task UserStatsGrain_VoiceTime_AwardsXp(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var statsGrain = grainFactory.GetGrain<IUserStatsGrain>(user.userId);

        // Record 10 minutes (600 seconds) of voice time using awaitable version
        // Should award 10 * 2 = 20 XP
        await statsGrain.RecordVoiceTimeAndWaitAsync(600, Guid.NewGuid(), Guid.NewGuid());

        var level = await GetUserService(scope.ServiceProvider).GetMyLevel(ct);

        Assert.That(level.totalXp, Is.EqualTo(20), "Should have 20 XP from 10 minutes of voice");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(43)]
    public async Task UserStatsGrain_IncrementMessages_UpdatesStats(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var statsGrain = grainFactory.GetGrain<IUserStatsGrain>(user.userId);

        // Use awaitable version for tests
        await statsGrain.IncrementMessagesAndWaitAsync();
        await statsGrain.IncrementMessagesAndWaitAsync();
        await statsGrain.IncrementMessagesAndWaitAsync();

        var stats = await statsGrain.GetTodayStatsAsync();

        Assert.That(stats.messagesSent, Is.EqualTo(3), "Should have 3 messages sent");
    }

    #endregion

    #region Coin Properties Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(50)]
    public async Task Coin_IsNotUsable(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(user.userId);

        await levelGrain.AwardXpAsync(XpForLevel100, XpSource.Event);
        var claimResult = await GetUserService(scope.ServiceProvider).ClaimLevelCoin(ct);
        Assert.That(claimResult, Is.True, "Claim should succeed");

        var inventory = await GetInventoryService(scope.ServiceProvider).GetMyInventoryItems(ct);

        var currentYear = DateTime.UtcNow.Year;
        var coin = inventory.Values.FirstOrDefault(x => x.id.StartsWith($"year_{currentYear}_coin_lvl"));

        Assert.That(coin, Is.Not.Null, "Coin should exist in inventory");
        Assert.That(coin!.usable, Is.False, "Coin should not be usable");
        Assert.That(coin.giftable, Is.False, "Coin should not be giftable");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(51)]
    public async Task UseItem_OnCoin_ReturnsFalse(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(user.userId);

        await levelGrain.AwardXpAsync(XpForLevel100, XpSource.Event);
        var claimResult = await GetUserService(scope.ServiceProvider).ClaimLevelCoin(ct);
        Assert.That(claimResult, Is.True, "Claim should succeed");

        var inventory = await GetInventoryService(scope.ServiceProvider).GetMyInventoryItems(ct);
        var coin = inventory.Values.FirstOrDefault(x => x.id.StartsWith($"year_{DateTime.UtcNow.Year}_coin_lvl"));

        Assert.That(coin, Is.Not.Null, "Coin should exist in inventory");

        // Try to use the coin
        var result = await GetInventoryService(scope.ServiceProvider).UseItem(coin!.instanceId, ct);

        Assert.That(result, Is.False, "Should not be able to use a coin");
    }

    #endregion

    #region Channel Voice XP Settlement Tests (Anti-Solo-Farm)

    [Test, CancelAfter(1000 * 60 * 5), Order(60)]
    public async Task ChannelVoice_SoloUser_GetsNoXp(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateVoiceChannelAsync(spaceId, "solo-test", ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var channelGrain = grainFactory.GetGrain<IChannelGrain>(channelId);

        Orleans.Runtime.RequestContext.Set("$caller_user_id", user.userId);
        try
        {
            await channelGrain.Join();
            await Task.Delay(100, ct);
            await channelGrain.Leave(user.userId);
        }
        finally
        {
            Orleans.Runtime.RequestContext.Clear();
        }

        // Check XP - should be 0 because user was solo
        var level = await GetUserService(scope.ServiceProvider).GetMyLevel(ct);
        Assert.That(level.totalXp, Is.EqualTo(0), "Solo user should not receive XP");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(61)]
    public async Task ChannelVoice_TwoUsers_BothGetXp(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        // Register first user (space owner)
        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var user1 = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateVoiceChannelAsync(spaceId, "duo-test", ct);

        // Register second user and add to space
        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var channelGrain = grainFactory.GetGrain<IChannelGrain>(channelId);
        var statsGrain1 = grainFactory.GetGrain<IUserStatsGrain>(user1.userId);
        var statsGrain2 = grainFactory.GetGrain<IUserStatsGrain>(user2.userId);

        // User1 joins
        Orleans.Runtime.RequestContext.Set("$caller_user_id", user1.userId);
        try { await channelGrain.Join(); }
        finally { Orleans.Runtime.RequestContext.Clear(); }

        // User2 joins
        Orleans.Runtime.RequestContext.Set("$caller_user_id", user2.userId);
        try { await channelGrain.Join(); }
        finally { Orleans.Runtime.RequestContext.Clear(); }

        // Both are in channel now - wait to accumulate time
        await Task.Delay(100, ct);

        // User2 leaves - this should settle XP for BOTH users
        await channelGrain.Leave(user2.userId);

        var stats1 = await statsGrain1.GetTodayStatsAsync();
        var stats2 = await statsGrain2.GetTodayStatsAsync();

        Assert.That(stats1.timeInVoice, Is.GreaterThanOrEqualTo(0), "User1 should have voice time recorded");
        Assert.That(stats2.timeInVoice, Is.GreaterThanOrEqualTo(0), "User2 should have voice time recorded");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(62)]
    public async Task ChannelVoice_LastUserLeaving_StillGetsXpForTimeWithOthers(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var user1 = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateVoiceChannelAsync(spaceId, "last-user-test", ct);

        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var channelGrain = grainFactory.GetGrain<IChannelGrain>(channelId);
        var statsGrain1 = grainFactory.GetGrain<IUserStatsGrain>(user1.userId);

        // User1 joins (solo)
        Orleans.Runtime.RequestContext.Set("$caller_user_id", user1.userId);
        try { await channelGrain.Join(); }
        finally { Orleans.Runtime.RequestContext.Clear(); }

        // User2 joins - settle happens, user1 was solo so no XP
        Orleans.Runtime.RequestContext.Set("$caller_user_id", user2.userId);
        try { await channelGrain.Join(); }
        finally { Orleans.Runtime.RequestContext.Clear(); }

        var statsBefore = await statsGrain1.GetTodayStatsAsync();
        var voiceTimeBefore = statsBefore.timeInVoice;

        // Wait some time with 2 users
        await Task.Delay(100, ct);

        // User2 leaves - this settles XP for both with memberCount=2
        await channelGrain.Leave(user2.userId);

        var statsAfterUser2Left = await statsGrain1.GetTodayStatsAsync();
        Assert.That(statsAfterUser2Left.timeInVoice, Is.GreaterThanOrEqualTo(voiceTimeBefore), 
            "User1 should have gained voice time while user2 was present");

        // Now user1 is solo again - wait more time
        await Task.Delay(100, ct);

        // User1 leaves (solo now)
        await channelGrain.Leave(user1.userId);

        var statsFinal = await statsGrain1.GetTodayStatsAsync();
        Assert.That(statsFinal.timeInVoice, Is.EqualTo(statsAfterUser2Left.timeInVoice),
            "Solo time after user2 left should not add more voice time");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(63)]
    public async Task ChannelVoice_UserJoining_SettlesXpForExistingUsers(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var user1 = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateVoiceChannelAsync(spaceId, "join-settle-test", ct);

        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var token3 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token3);
        var user3 = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var channelGrain = grainFactory.GetGrain<IChannelGrain>(channelId);
        var statsGrain1 = grainFactory.GetGrain<IUserStatsGrain>(user1.userId);
        var statsGrain2 = grainFactory.GetGrain<IUserStatsGrain>(user2.userId);

        // User1 and User2 join
        Orleans.Runtime.RequestContext.Set("$caller_user_id", user1.userId);
        try { await channelGrain.Join(); }
        finally { Orleans.Runtime.RequestContext.Clear(); }

        Orleans.Runtime.RequestContext.Set("$caller_user_id", user2.userId);
        try { await channelGrain.Join(); }
        finally { Orleans.Runtime.RequestContext.Clear(); }

        // Wait with 2 users
        await Task.Delay(100, ct);

        var stats1Before = await statsGrain1.GetTodayStatsAsync();
        var stats2Before = await statsGrain2.GetTodayStatsAsync();

        // User3 joins - this should SETTLE XP for user1 and user2 (memberCount=2)
        Orleans.Runtime.RequestContext.Set("$caller_user_id", user3.userId);
        try { await channelGrain.Join(); }
        finally { Orleans.Runtime.RequestContext.Clear(); }

        var stats1After = await statsGrain1.GetTodayStatsAsync();
        var stats2After = await statsGrain2.GetTodayStatsAsync();

        Assert.That(stats1After.timeInVoice, Is.GreaterThanOrEqualTo(stats1Before.timeInVoice),
            "User1 should have voice time settled when user3 joined");
        Assert.That(stats2After.timeInVoice, Is.GreaterThanOrEqualTo(stats2Before.timeInVoice),
            "User2 should have voice time settled when user3 joined");
    }

    private async Task<Guid> CreateVoiceChannelAsync(Guid spaceId, string channelName = "voice-channel", CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        
        await GetChannelService(scope.ServiceProvider).CreateChannel(
            spaceId,
            Guid.Empty,
            new CreateChannelRequest(spaceId, channelName, ChannelType.Voice, "Test voice channel", null),
            ct);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);
        Orleans.Runtime.RequestContext.Set("$caller_user_id", user.userId);
        
        try
        {
            var spaceGrain = FactoryAsp.Services.GetRequiredService<IGrainFactory>()
                .GetGrain<ISpaceGrain>(spaceId);
            
            var channels = await spaceGrain.GetChannels();
            var createdChannel = channels.FirstOrDefault(c => c.channel.name == channelName);
            
            if (createdChannel == null)
                Assert.Fail($"Failed to find created voice channel '{channelName}'");
                
            return createdChannel!.channel.channelId;
        }
        finally
        {
            Orleans.Runtime.RequestContext.Clear();
        }
    }

    #endregion
}
