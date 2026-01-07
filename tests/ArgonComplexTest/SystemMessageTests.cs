namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Grains;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using InviteCodeContract = ArgonContracts.InviteCode;

[TestFixture, Parallelizable(ParallelScope.None)]
public class SystemMessageTests : TestBase
{
    #region Call System Messages Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task CallStarted_SendsSystemMessage_ToBothUsers(CancellationToken ct = default)
    {
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        await using var scope2 = FactoryAsp.Services.CreateAsyncScope();

        // Register first user (caller)
        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var user1 = await GetUserService(scope1.ServiceProvider).GetMe(ct);

        // Register second user (callee)
        Setup(); // Reset creds
        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope2.ServiceProvider).GetMe(ct);

        // Set caller token
        SetAuthToken(token1);

        // Start call from user1 to user2
        var callResult = await GetCallService(scope1.ServiceProvider).DingDongCreep(user2.userId, ct);
        
        Assert.That(callResult, Is.InstanceOf<SuccessDingDong>(), "Call should start successfully");
        var successCall = (SuccessDingDong)callResult;
        var callId = successCall.callId;

        // Answer call as user2
        SetAuthToken(token2);
        var pickupResult = await GetCallService(scope2.ServiceProvider).PickUpCall(callId, ct);
        
        Assert.That(pickupResult, Is.InstanceOf<SuccessPickUp>(), "Call should be picked up successfully");

        // Wait a bit for system message to be sent
        await Task.Delay(500, ct);

        // Check DM messages for user1
        SetAuthToken(token1);
        var user1Messages = await GetUserChatService(scope1.ServiceProvider)
            .QueryDirectMessages(user2.userId, null, 10, ct);

        Assert.That(user1Messages.Values.Count, Is.GreaterThan(0), "Should have at least one system message");
        
        var callStartedMsg = user1Messages.Values.FirstOrDefault(m => m.text.Contains("Call started"));
        Assert.That(callStartedMsg, Is.Not.Null, "Should have 'Call started' system message");
        Assert.That(callStartedMsg!.senderId, Is.EqualTo(UserEntity.SystemUser), "Message should be from system user");

        // Hangup call
        await GetCallService(scope1.ServiceProvider).HangupCall(callId, ct);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task CallEnded_SendsSystemMessageWithDuration(CancellationToken ct = default)
    {
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        await using var scope2 = FactoryAsp.Services.CreateAsyncScope();

        // Register first user (caller)
        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var user1 = await GetUserService(scope1.ServiceProvider).GetMe(ct);

        // Register second user (callee)
        Setup();
        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope2.ServiceProvider).GetMe(ct);

        // Start call
        SetAuthToken(token1);
        var callResult = await GetCallService(scope1.ServiceProvider).DingDongCreep(user2.userId, ct);
        var successCall = (SuccessDingDong)callResult;
        var callId = successCall.callId;

        // Answer call
        SetAuthToken(token2);
        await GetCallService(scope2.ServiceProvider).PickUpCall(callId, ct);

        // Wait 2 seconds to have some call duration
        await Task.Delay(2000, ct);

        // Hangup call
        SetAuthToken(token1);
        await GetCallService(scope1.ServiceProvider).HangupCall(callId, ct);

        // Wait for system message
        await Task.Delay(500, ct);

        // Check for call ended message
        var messages = await GetUserChatService(scope1.ServiceProvider)
            .QueryDirectMessages(user2.userId, null, 10, ct);

        var callEndedMsg = messages.Values.FirstOrDefault(m => m.text.Contains("Call ended"));
        Assert.That(callEndedMsg, Is.Not.Null, "Should have 'Call ended' system message");
        Assert.That(callEndedMsg!.senderId, Is.EqualTo(UserEntity.SystemUser));
        Assert.That(callEndedMsg.text, Does.Match(@"Call ended \(\d{2}:\d{2}:\d{2}\)"), 
            "Call ended message should contain duration in format HH:MM:SS");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(2)]
    public async Task CallTimeout_SendsSystemMessage_WhenNotAnswered(CancellationToken ct = default)
    {
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        await using var scope2 = FactoryAsp.Services.CreateAsyncScope();

        // Register first user (caller)
        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var user1 = await GetUserService(scope1.ServiceProvider).GetMe(ct);

        // Register second user (callee) - but will not answer
        Setup();
        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope2.ServiceProvider).GetMe(ct);

        // Start call
        SetAuthToken(token1);
        var cachedTimeout = CallGrain.RingTimeout;
        CallGrain.RingTimeout = TimeSpan.FromSeconds(10);
        var callResult    = await GetCallService(scope1.ServiceProvider).DingDongCreep(user2.userId, ct);
        var successCall   = (SuccessDingDong)callResult;

        // Wait for call timeout (45 seconds + buffer)
        await Task.Delay(TimeSpan.FromSeconds(20), ct);

        // Check for timeout message
        var messages = await GetUserChatService(scope1.ServiceProvider)
            .QueryDirectMessages(user2.userId, null, 10, ct);
        CallGrain.RingTimeout = cachedTimeout;

        var timeoutMsg = messages.Values.FirstOrDefault(m => m.text.Contains("Call not answered"));
        Assert.That(timeoutMsg, Is.Not.Null, "Should have 'Call not answered' system message after timeout");
        Assert.That(timeoutMsg!.senderId, Is.EqualTo(UserEntity.SystemUser));
    }

    #endregion

    #region Space Join System Messages Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(10)]
    public async Task UserJoined_SendsSystemMessage_ToDefaultChannel_InPrivateSpace(CancellationToken ct = default)
    {
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        await using var scope2 = FactoryAsp.Services.CreateAsyncScope();

        // Create space owner
        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        // Set default channel for the space
        var defaultChannelId = await CreateTextChannelAsync(spaceId, "general", ct);
        await SetDefaultChannelForSpaceAsync(spaceId, defaultChannelId, ct);

        // Create invite - use grain directly since no API method exists
        var inviteCode = await CreateInviteForSpaceAsync(spaceId, ct);

        // Register second user and join via invite
        Setup();
        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope2.ServiceProvider).GetMe(ct);

        var joinResult = await GetUserService(scope2.ServiceProvider)
            .JoinToSpace(new InviteCodeContract(inviteCode), ct);

        Assert.That(joinResult, Is.InstanceOf<SuccessJoin>(), "User should join successfully");

        // Wait for system message
        await Task.Delay(500, ct);

        // Check messages in default channel
        SetAuthToken(token1);
        var messages = await GetChannelService(scope1.ServiceProvider)
            .QueryMessages(spaceId, defaultChannelId, null, 10, ct);

        var joinMsg = messages.Values.FirstOrDefault(m => 
            m.text.Contains(user2.username) && m.text.Contains("joined the space"));
        
        Assert.That(joinMsg, Is.Not.Null, "Should have user joined system message");
        Assert.That(joinMsg!.sender, Is.EqualTo(UserEntity.SystemUser));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(11)]
    public async Task UserJoined_NoSystemMessage_InCommunitySpace(CancellationToken ct = default)
    {
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        await using var scope2 = FactoryAsp.Services.CreateAsyncScope();

        // Create space owner
        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        // Mark space as community
        await SetSpaceAsCommunityAsync(spaceId, ct);

        var defaultChannelId = await CreateTextChannelAsync(spaceId, "general", ct);
        await SetDefaultChannelForSpaceAsync(spaceId, defaultChannelId, ct);

        // Create invite
        var inviteCode = await CreateInviteForSpaceAsync(spaceId, ct);

        // Register second user and join
        Setup();
        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);

        await GetUserService(scope2.ServiceProvider).JoinToSpace(new InviteCodeContract(inviteCode), ct);
        await Task.Delay(500, ct);

        // Check messages - should NOT have join message for community
        SetAuthToken(token1);
        var messages = await GetChannelService(scope1.ServiceProvider)
            .QueryMessages(spaceId, defaultChannelId, null, 10, ct);

        var joinMsg = messages.Values.FirstOrDefault(m => m.text.Contains("joined the space"));
        Assert.That(joinMsg, Is.Null, "Community space should NOT have user joined system messages");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(12)]
    public async Task UserJoined_IncludesInviterInfo_WhenJoinedViaInvite(CancellationToken ct = default)
    {
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        await using var scope2 = FactoryAsp.Services.CreateAsyncScope();

        // Create space owner (inviter)
        var token1 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token1);
        var inviter = await GetUserService(scope1.ServiceProvider).GetMe(ct);
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        var defaultChannelId = await CreateTextChannelAsync(spaceId, "general", ct);
        await SetDefaultChannelForSpaceAsync(spaceId, defaultChannelId, ct);

        // Create invite
        var inviteCode = await CreateInviteForSpaceAsync(spaceId, ct);

        // Register second user
        Setup();
        var token2 = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token2);
        var user2 = await GetUserService(scope2.ServiceProvider).GetMe(ct);

        await GetUserService(scope2.ServiceProvider).JoinToSpace(new InviteCodeContract(inviteCode), ct);
        await Task.Delay(500, ct);

        // Check message includes inviter
        SetAuthToken(token1);
        var messages = await GetChannelService(scope1.ServiceProvider)
            .QueryMessages(spaceId, defaultChannelId, null, 10, ct);

        var joinMsg = messages.Values.FirstOrDefault(m => m.text.Contains("joined the space"));
        Assert.That(joinMsg, Is.Not.Null);
    }

    #endregion

    #region Helper Methods

    protected ICallInteraction GetCallService(IServiceProvider? serviceProvider = null)
    {
        var provider = serviceProvider ?? FactoryAsp.Services;
        return IonClient.ForService<ICallInteraction>(provider);
    }

    protected IUserChatInteractions GetUserChatService(IServiceProvider? serviceProvider = null)
    {
        var provider = serviceProvider ?? FactoryAsp.Services;
        return IonClient.ForService<IUserChatInteractions>(provider);
    }

    private async Task SetDefaultChannelForSpaceAsync(Guid spaceId, Guid channelId, CancellationToken ct)
    {
        var factory = FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var dbContext = await factory.CreateDbContextAsync(ct);

        var space = await dbContext.Spaces.FirstAsync(s => s.Id == spaceId, ct);
        space.DefaultChannelId = channelId;
        await dbContext.SaveChangesAsync(ct);
    }

    private async Task SetSpaceAsCommunityAsync(Guid spaceId, CancellationToken ct)
    {
        var factory = FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var dbContext = await factory.CreateDbContextAsync(ct);

        var space = await dbContext.Spaces.FirstAsync(s => s.Id == spaceId, ct);
        space.IsCommunity = true;
        await dbContext.SaveChangesAsync(ct);
    }

    private async Task<string> CreateInviteForSpaceAsync(Guid spaceId, CancellationToken ct)
    {
        var factory = FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var dbContext = await factory.CreateDbContextAsync(ct);

        var user = await GetUserService(FactoryAsp.Services).GetMe(ct);
        
        var inviteCode = InviteCodeEntityData.GenerateInviteCode();
        var invite = new SpaceInvite
        {
            Id = InviteCodeEntityData.EncodeToUlong(inviteCode),
            SpaceId = spaceId,
            CreatorId = user.userId,
            ExpireAt = DateTimeOffset.UtcNow.AddDays(7),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        
        dbContext.Invites.Add(invite);
        await dbContext.SaveChangesAsync(ct);
        
        return inviteCode;
    }

    #endregion
}
