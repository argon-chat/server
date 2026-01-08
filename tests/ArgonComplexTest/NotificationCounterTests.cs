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

    #region GetNotificationCounters Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task GetNotificationCounters_NewUser_ReturnsAllZeroCounters(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var counters = (await GetUserService(scope.ServiceProvider).GetNotificationCounters(ct)).Values;

        Assert.That(counters.Count, Is.EqualTo(3), "Should return 3 counter types");

        var inventoryCounter = counters.FirstOrDefault(c => c.counterType == NotificationCounterType.UnreadInventoryItems);
        var friendsCounter = counters.FirstOrDefault(c => c.counterType == NotificationCounterType.PendingFriendRequests);
        var messagesCounter = counters.FirstOrDefault(c => c.counterType == NotificationCounterType.UnreadDirectMessages);

        Assert.That(inventoryCounter, Is.Not.Null, "Should have inventory counter");
        Assert.That(friendsCounter, Is.Not.Null, "Should have friends counter");
        Assert.That(messagesCounter, Is.Not.Null, "Should have messages counter");

        Assert.That(inventoryCounter!.count, Is.EqualTo(0), "Inventory counter should be 0");
        Assert.That(friendsCounter!.count, Is.EqualTo(0), "Friends counter should be 0");
        Assert.That(messagesCounter!.count, Is.EqualTo(0), "Messages counter should be 0");
    }

    #endregion

    #region Inventory Notification Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(10)]
    public async Task GiveItem_IncrementsInventoryCounter(CancellationToken ct = default)
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

        var counters = await GetUserService(scope.ServiceProvider).GetNotificationCounters(ct);
        var inventoryCounter = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.UnreadInventoryItems);

        Assert.That(inventoryCounter, Is.Not.Null);
        Assert.That(inventoryCounter!.count, Is.EqualTo(1), "Should have 1 unread inventory item");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(11)]
    public async Task GiveMultipleItems_IncrementsInventoryCounterCorrectly(CancellationToken ct = default)
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

        var counters = await GetUserService(scope.ServiceProvider).GetNotificationCounters(ct);
        var inventoryCounter = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.UnreadInventoryItems);

        Assert.That(inventoryCounter!.count, Is.EqualTo(3), "Should have 3 unread inventory items");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(12)]
    public async Task MarkItemsSeen_DecrementsInventoryCounter(CancellationToken ct = default)
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

        var counters = await GetUserService(scope.ServiceProvider).GetNotificationCounters(ct);
        var inventoryCounter = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.UnreadInventoryItems);

        Assert.That(inventoryCounter!.count, Is.EqualTo(0), "Should have 0 unread inventory items after marking seen");
    }

    #endregion

    #region Friend Request Notification Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(20)]
    public async Task SendFriendRequest_IncrementsPendingFriendRequestsCounter(CancellationToken ct = default)
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
        var counters = await GetUserService(scope2.ServiceProvider).GetNotificationCounters(ct);
        var friendsCounter = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.PendingFriendRequests);

        Assert.That(friendsCounter!.count, Is.EqualTo(1), "Should have 1 pending friend request");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(21)]
    public async Task AcceptFriendRequest_DecrementsPendingFriendRequestsCounter(CancellationToken ct = default)
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

        var counters = await GetUserService(scope2.ServiceProvider).GetNotificationCounters(ct);
        var friendsCounter = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.PendingFriendRequests);

        Assert.That(friendsCounter!.count, Is.EqualTo(0), "Should have 0 pending friend requests after accepting");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(22)]
    public async Task DeclineFriendRequest_DecrementsPendingFriendRequestsCounter(CancellationToken ct = default)
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
        await GetFriendsService(scope2.ServiceProvider).DeclineFriendRequest(user1.userId, ct);

        await Task.Delay(500, ct);

        var counters = await GetUserService(scope2.ServiceProvider).GetNotificationCounters(ct);
        var friendsCounter = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.PendingFriendRequests);

        Assert.That(friendsCounter!.count, Is.EqualTo(0), "Should have 0 pending friend requests after declining");
    }

    #endregion

    #region Direct Message Notification Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(30)]
    public async Task SendDirectMessage_IncrementsUnreadDirectMessagesCounter(CancellationToken ct = default)
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
        await GetUserChatService(scope1.ServiceProvider).SendDirectMessage(
            user2.userId, 
            "Hello!", 
            new ion.runtime.IonArray<IMessageEntity>([]), 
            1, 
            null, 
            ct);

        await Task.Delay(500, ct);

        SetAuthToken(token2);
        var counters = await GetUserService(scope2.ServiceProvider).GetNotificationCounters(ct);
        var messagesCounter = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.UnreadDirectMessages);

        Assert.That(messagesCounter!.count, Is.EqualTo(1), "Should have 1 unread direct message");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(31)]
    public async Task SendMultipleDirectMessages_IncrementsUnreadDirectMessagesCounterCorrectly(CancellationToken ct = default)
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
        for (var i = 1; i <= 3; i++)
        {
            await GetUserChatService(scope1.ServiceProvider).SendDirectMessage(
                user2.userId, 
                $"Message {i}", 
                new ion.runtime.IonArray<IMessageEntity>([]), 
                i, 
                null, 
                ct);
        }

        await Task.Delay(500, ct);

        SetAuthToken(token2);
        var counters = await GetUserService(scope2.ServiceProvider).GetNotificationCounters(ct);
        var messagesCounter = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.UnreadDirectMessages);

        Assert.That(messagesCounter!.count, Is.EqualTo(3), "Should have 3 unread direct messages");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(32)]
    public async Task MarkChatRead_DecrementsUnreadDirectMessagesCounter(CancellationToken ct = default)
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
        await GetUserChatService(scope1.ServiceProvider).SendDirectMessage(
            user2.userId, 
            "Test message", 
            new ion.runtime.IonArray<IMessageEntity>([]), 
            1, 
            null, 
            ct);

        await Task.Delay(500, ct);

        SetAuthToken(token2);
        await GetUserChatService(scope2.ServiceProvider).MarkChatRead(user1.userId, ct);

        await Task.Delay(500, ct);

        var counters = await GetUserService(scope2.ServiceProvider).GetNotificationCounters(ct);
        var messagesCounter = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.UnreadDirectMessages);

        Assert.That(messagesCounter!.count, Is.EqualTo(0), "Should have 0 unread direct messages after marking read");
    }

    #endregion

    #region Combined Counter Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(40)]
    public async Task MultipleNotifications_AllCountersUpdateIndependently(CancellationToken ct = default)
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

        SetAuthToken(token1);
        await GetUserChatService(scope1.ServiceProvider).SendDirectMessage(
            user2.userId, 
            "Combined test", 
            new ion.runtime.IonArray<IMessageEntity>([]), 
            1, 
            null, 
            ct);

        await Task.Delay(500, ct);

        SetAuthToken(token2);
        var counters = await GetUserService(scope2.ServiceProvider).GetNotificationCounters(ct);

        var inventoryCounter = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.UnreadInventoryItems);
        var friendsCounter   = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.PendingFriendRequests);
        var messagesCounter  = counters.Values.FirstOrDefault(c => c.counterType == NotificationCounterType.UnreadDirectMessages);

        Assert.That(inventoryCounter!.count, Is.EqualTo(1), "Should have 1 unread inventory item");
        Assert.That(friendsCounter!.count, Is.EqualTo(1), "Should have 1 pending friend request");
        Assert.That(messagesCounter!.count, Is.EqualTo(1), "Should have 1 unread direct message");
    }

    #endregion
}
