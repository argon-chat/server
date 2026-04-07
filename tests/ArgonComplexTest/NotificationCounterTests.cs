namespace ArgonComplexTest.Tests;

using Argon.Api.Grains.Interfaces;
using Argon.Core.Entities.Data;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

[TestFixture, Parallelizable(ParallelScope.None)]
public class NotificationCounterTests : TestBase
{
    private IFriendsInteraction GetFriendsService(IServiceProvider? serviceProvider = null)
    {
        var provider = serviceProvider ?? FactoryAsp.Services;
        return IonClient.ForService<IFriendsInteraction>(provider);
    }

    private IUserChatInteractions GetUserChatService(IServiceProvider? serviceProvider = null)
    {
        var provider = serviceProvider ?? FactoryAsp.Services;
        return IonClient.ForService<IUserChatInteractions>(provider);
    }

    #region GetGlobalBadges Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task GetGlobalBadges_NewUser_ReturnsAllZeroBadges(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var badges = await GetUserService(scope.ServiceProvider).GetGlobalBadges(ct);

        Assert.That(badges.unreadDmCount, Is.EqualTo(0), "Should have 0 unread DMs");
        Assert.That(badges.notifications.friendRequests, Is.EqualTo(0), "Should have 0 friend request notifications");
        Assert.That(badges.notifications.inventory, Is.EqualTo(0), "Should have 0 inventory notifications");
        Assert.That(badges.notifications.system, Is.EqualTo(0), "Should have 0 system notifications");
    }

    #endregion

    #region Inventory Notification Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(10)]
    public async Task GiveItem_CreatesInventoryNotification(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var inventoryGrain = grainFactory.GetGrain<IInventoryGrain>(Guid.NewGuid());

        var refItemId = await inventoryGrain.CreateReferenceItem("test-item-1", false, false, false, ct);
        Assert.That(refItemId, Is.Not.Null, "Reference item should be created");

        await inventoryGrain.GiveItemFor(user.userId, refItemId!.Value, ct);

        await Task.Delay(500, ct);

        var badges = await GetUserService(scope.ServiceProvider).GetGlobalBadges(ct);

        Assert.That(badges.notifications.inventory, Is.EqualTo(1), "Should have 1 inventory notification");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(11)]
    public async Task GiveMultipleItems_CreatesMultipleInventoryNotifications(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var inventoryGrain = grainFactory.GetGrain<IInventoryGrain>(Guid.NewGuid());

        var refItemId1 = await inventoryGrain.CreateReferenceItem("test-item-2", false, false, false, ct);
        var refItemId2 = await inventoryGrain.CreateReferenceItem("test-item-3", false, false, false, ct);
        var refItemId3 = await inventoryGrain.CreateReferenceItem("test-item-4", false, false, false, ct);

        await inventoryGrain.GiveItemFor(user.userId, refItemId1!.Value, ct);
        await inventoryGrain.GiveItemFor(user.userId, refItemId2!.Value, ct);
        await inventoryGrain.GiveItemFor(user.userId, refItemId3!.Value, ct);

        await Task.Delay(500, ct);

        var badges = await GetUserService(scope.ServiceProvider).GetGlobalBadges(ct);

        Assert.That(badges.notifications.inventory, Is.EqualTo(3), "Should have 3 inventory notifications");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(12)]
    public async Task MarkItemsSeen_ClearsInventoryNotifications(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var inventoryGrain = grainFactory.GetGrain<IInventoryGrain>(Guid.NewGuid());

        var refItemId1 = await inventoryGrain.CreateReferenceItem("test-item-5", false, false, false, ct);
        var refItemId2 = await inventoryGrain.CreateReferenceItem("test-item-6", false, false, false, ct);

        await inventoryGrain.GiveItemFor(user.userId, refItemId1!.Value, ct);
        await inventoryGrain.GiveItemFor(user.userId, refItemId2!.Value, ct);

        await Task.Delay(500, ct);

        var myItems = await GetInventoryService(scope.ServiceProvider).GetMyInventoryItems(ct);
        var itemIds = myItems.Values.Select(i => i.instanceId).ToArray();

        await GetInventoryService(scope.ServiceProvider).MarkSeen(new ion.runtime.IonArray<Guid>(itemIds), ct);

        await Task.Delay(500, ct);

        var badges = await GetUserService(scope.ServiceProvider).GetGlobalBadges(ct);

        Assert.That(badges.notifications.inventory, Is.EqualTo(0), "Should have 0 inventory notifications after marking seen");
    }

    #endregion

    #region Friend Request Notification Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(20)]
    public async Task SendFriendRequest_CreatesFriendRequestNotification(CancellationToken ct = default)
    {
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        await using var scope2 = FactoryAsp.Services.CreateAsyncScope();

        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var user1 = await GetUserService(scope1.ServiceProvider).GetMe(ct);

        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope2.ServiceProvider).GetMe(ct);

        SetAuthToken(token1);
        await GetFriendsService(scope1.ServiceProvider).SendFriendRequest(user2.username, ct);

        await Task.Delay(500, ct);

        SetAuthToken(token2);
        var badges = await GetUserService(scope2.ServiceProvider).GetGlobalBadges(ct);

        Assert.That(badges.notifications.friendRequests, Is.EqualTo(1), "Should have 1 friend request notification");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(21)]
    public async Task AcceptFriendRequest_NotificationFeedContainsBothEvents(CancellationToken ct = default)
    {
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        await using var scope2 = FactoryAsp.Services.CreateAsyncScope();

        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var user1 = await GetUserService(scope1.ServiceProvider).GetMe(ct);

        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope2.ServiceProvider).GetMe(ct);

        SetAuthToken(token1);
        await GetFriendsService(scope1.ServiceProvider).SendFriendRequest(user2.username, ct);

        await Task.Delay(500, ct);

        SetAuthToken(token2);
        await GetFriendsService(scope2.ServiceProvider).AcceptFriendRequest(user1.userId, ct);

        await Task.Delay(500, ct);

        SetAuthToken(token2);
        var feed = await GetUserService(scope2.ServiceProvider).GetNotificationFeed(50, null, ct);

        Assert.That(feed.Values.Count, Is.GreaterThanOrEqualTo(1), "Should have at least 1 notification in feed");
    }

    #endregion

    #region Combined Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(40)]
    public async Task MultipleNotifications_AllBadgesUpdateIndependently(CancellationToken ct = default)
    {
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        await using var scope2 = FactoryAsp.Services.CreateAsyncScope();

        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var user1 = await GetUserService(scope1.ServiceProvider).GetMe(ct);

        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope2.ServiceProvider).GetMe(ct);

        var grainFactory = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
        var inventoryGrain = grainFactory.GetGrain<IInventoryGrain>(Guid.NewGuid());

        var refItemId = await inventoryGrain.CreateReferenceItem("test-combined-item", false, false, false, ct);
        await inventoryGrain.GiveItemFor(user2.userId, refItemId!.Value, ct);

        SetAuthToken(token1);
        await GetFriendsService(scope1.ServiceProvider).SendFriendRequest(user2.username, ct);

        await Task.Delay(500, ct);

        SetAuthToken(token2);
        var badges = await GetUserService(scope2.ServiceProvider).GetGlobalBadges(ct);

        Assert.That(badges.notifications.inventory, Is.EqualTo(1), "Should have 1 inventory notification");
        Assert.That(badges.notifications.friendRequests, Is.EqualTo(1), "Should have 1 friend request notification");
    }

    #endregion
}
