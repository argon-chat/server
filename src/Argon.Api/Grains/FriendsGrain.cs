namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Grains.Interfaces;
using Core.Entities.Data;
using Orleans.Concurrency;

[StatelessWorker]
public class FriendsGrain(
    IDbContextFactory<ApplicationDbContext> context, 
    ILogger<IFriendsGrain> logger,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    ISystemNotificationService systemNotification) : Grain, IFriendsGrain
{
    private async Task NotifyAsync<T>(Guid userId, T payload) where T : IArgonEvent
    {
        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId);

        if (sessions.Count == 0) return;

        await notifier.NotifySessionsAsync(sessions, payload);
    }

    /// <summary>
    /// Two people who have just become friends have each missed every status event the other has
    /// ever fired, and presence is only pushed forward from here - without this both sides read as
    /// offline until the other happens to change something.
    /// </summary>
    private Task ExchangePresenceAsync(Guid a, Guid b)
        => Task.WhenAll(
            GrainFactory.GetGrain<IUserGrain>(a).PushFriendPresenceAsync().AsTask(),
            GrainFactory.GetGrain<IUserGrain>(b).PushFriendPresenceAsync().AsTask());

    public async Task<List<UserBlock>> GetBlockListAsync(int limit, int offset, CancellationToken ct = default)
    {
        var             meUserId = this.GetUserId();
        await using var ctx      = await context.CreateDbContextAsync(ct);

        var result = await ctx.UserBlocklist
           .AsNoTracking()
           .Where(x => x.UserId == meUserId)
           .OrderByDescending(x => x.CreatedAt)
           .Skip(offset)
           .Take(limit)
           .ToListAsync(ct);
        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task<List<FriendRequest>> GetMyFriendPendingListAsync(int limit, int offset, CancellationToken ct = default)
    {
        var             meUserId = this.GetUserId();
        await using var ctx      = await context.CreateDbContextAsync(ct);
        var result = await ctx.FriendRequest
           .AsNoTracking()
           .Where(x => x.TargetId == meUserId)
           .OrderByDescending(x => x.RequestedAt)
           .Skip(offset)
           .Take(limit)
           .ToListAsync(ct);

        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task<List<FriendRequest>> GetMyFriendOutgoingListAsync(int limit, int offset, CancellationToken ct = default)
    {
        var             meUserId = this.GetUserId();
        await using var ctx      = await context.CreateDbContextAsync(ct);

        var result = await ctx.FriendRequest
           .AsNoTracking()
           .Where(x => x.RequesterId == meUserId)
           .OrderByDescending(x => x.RequestedAt)
           .Skip(offset)
           .Take(limit)
           .ToListAsync(ct);

        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task<List<Friendship>> GetMyFriendshipsAsync(int limit, int offset, CancellationToken ct = default)
    {
        var             meUserId = this.GetUserId();
        await using var ctx      = await context.CreateDbContextAsync(ct);

        var result = await ctx.Friends
           .AsNoTracking()
           .Where(x => x.UserId == meUserId)
           .OrderBy(x => x.CreatedAt)
           .Skip(offset)
           .Take(limit)
           .ToListAsync(ct);

        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task<SendFriendStatus> SendFriendRequestAsync(string username, CancellationToken ct = default)
    {
        var me = this.GetUserId();
        logger.LogInformation("User {User} sending friend request to {Username}", me, username);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var target = await FindUserByUsernameAsync(ctx, username, ct);
        if (target is null)
        {
            logger.LogWarning("Target user not found: {Username}", username);
            return SendFriendStatus.TargetNotFound;
        }

        if (target == me)
        {
            logger.LogWarning("User {User} attempted to friend themselves", me);
            return SendFriendStatus.CannotFriendYourself;
        }

        var iBlock  = await ctx.UserBlocklist.AnyAsync(x => x.UserId == me && x.BlockedId == target, ct);
        var heBlock = await ctx.UserBlocklist.AnyAsync(x => x.UserId == target && x.BlockedId == me, ct);

        if (iBlock || heBlock)
        {
            logger.LogWarning("Friend request blocked due to blocklist: {User}->{Target}", me, target);
            return SendFriendStatus.Blocked;
        }

        var alreadyFriends = await ctx.Friends.AnyAsync(x => x.UserId == me && x.FriendId == target, ct);

        if (alreadyFriends)
        {
            logger.LogInformation("Friend request skipped because already friends: {User}->{Target}", me, target);
            return SendFriendStatus.AlreadyFriends;
        }

        var reverse = await ctx.FriendRequest
           .FirstOrDefaultAsync(x => x.RequesterId == target && x.TargetId == me, ct);

        if (reverse is not null)
        {
            logger.LogInformation("Auto-accepting reverse friend request: {User}<->{Target}", me, target);

            ctx.FriendRequest.Remove(reverse);

            ctx.Friends.Add(new FriendshipEntity
            {
                UserId    = me,
                FriendId  = target.Value,
                CreatedAt = DateTimeOffset.UtcNow
            });

            ctx.Friends.Add(new FriendshipEntity
            {
                UserId    = target.Value,
                FriendId  = me,
                CreatedAt = DateTimeOffset.UtcNow
            });

            await ctx.SaveChangesAsync(ct);

            var ts = DateTimeOffset.UtcNow.UtcDateTime;

            await NotifyAsync(me,
                new FriendRequestAcceptedEvent(target.Value, ts));

            await NotifyAsync(target.Value,
                new FriendRequestAcceptedEvent(me, ts));

            await ExchangePresenceAsync(me, target.Value);

            return SendFriendStatus.AutoAccepted;
        }

        var exists = await ctx.FriendRequest.AnyAsync(x => x.RequesterId == me && x.TargetId == target, ct);
        if (exists)
        {
            logger.LogInformation("Friend request already sent: {User}->{Target}", me, target);
            return SendFriendStatus.AlreadySent;
        }

        logger.LogInformation("Sending friend request: {User}->{Target}", me, target);

        ctx.FriendRequest.Add(new FriendRequestEntity
        {
            RequesterId = me,
            TargetId    = target.Value,
            RequestedAt = DateTimeOffset.UtcNow,
            ExpiredAt = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(31 * 6))
        });

        await ctx.SaveChangesAsync(ct);

        await NotifyAsync(target.Value,
            new FriendRequestReceivedEvent(me, DateTimeOffset.UtcNow.UtcDateTime));

        await NotifyAsync(me,
            new FriendRequestSentEvent(target.Value, DateTimeOffset.UtcNow.UtcDateTime));
        return SendFriendStatus.SuccessSent;
    }


    public async Task RemoveFriendAsync(Guid userId, CancellationToken ct = default)
    {
        var meUserId = this.GetUserId();
        logger.LogInformation("Removing friend: {User} -> {Friend}", meUserId, userId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var affected = await Between(ctx.Friends.IgnoreQueryFilters(), meUserId, userId).ExecuteDeleteAsync(ct);

        await NotifyAsync(meUserId, new FriendshipRemovedEvent(userId));
        await NotifyAsync(userId, new FriendshipRemovedEvent(meUserId));

        logger.LogInformation(
            "Removed friendship rows: {Count} for {UserId} and {FriendId}",
            affected, meUserId, userId);
    }

    public async Task AcceptFriendRequestAsync(Guid fromUserId, CancellationToken ct = default)
    {
        var me = this.GetUserId();
        logger.LogInformation("User {User} accepting friend request from {From}", me, fromUserId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var request = await ctx.FriendRequest
           .FirstOrDefaultAsync(x => x.RequesterId == fromUserId && x.TargetId == me, ct);

        if (request is null)
        {
            logger.LogWarning("No incoming friend request from {From} to {User}", fromUserId, me);
            return;
        }

        
        var iBlock  = await ctx.UserBlocklist.AnyAsync(x => x.UserId == me && x.BlockedId == fromUserId, ct);
        var heBlock = await ctx.UserBlocklist.AnyAsync(x => x.UserId == fromUserId && x.BlockedId == me, ct);

        if (iBlock || heBlock)
        {
            logger.LogWarning("Accept aborted due to blocklist: {User}<->{From}", me, fromUserId);
            ctx.FriendRequest.Remove(request);
            await ctx.SaveChangesAsync(ct);
            return;
        }

        var alreadyFriends = await ctx.Friends.AnyAsync(x => x.UserId == me && x.FriendId == fromUserId, ct);
        if (alreadyFriends)
        {
            logger.LogInformation("Friendship already exists, removing pending request: {User}<->{From}", me, fromUserId);
            ctx.FriendRequest.Remove(request);
            await ctx.SaveChangesAsync(ct);
            return;
        }

        logger.LogInformation("Creating friendships entries for {User} and {From}", me, fromUserId);

        ctx.FriendRequest.Remove(request);

        ctx.Friends.Add(new FriendshipEntity
        {
            UserId    = me,
            FriendId  = fromUserId,
            CreatedAt = DateTimeOffset.UtcNow
        });

        ctx.Friends.Add(new FriendshipEntity
        {
            UserId    = fromUserId,
            FriendId  = me,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await ctx.SaveChangesAsync(ct);

        await systemNotification.CreateAsync(fromUserId, SystemNotificationType.FriendRequestAccepted, me, "Friend request accepted", null, ct: ct);

        var ts = DateTimeOffset.UtcNow.UtcDateTime;

        await NotifyAsync(me,
            new FriendRequestAcceptedEvent(fromUserId, ts));

        await NotifyAsync(fromUserId,
            new FriendRequestAcceptedEvent(me, ts));

        await ExchangePresenceAsync(me, fromUserId);

        var chatId = ArgonId.New();

        var chatGrain = this.GrainFactory.GetGrain<IUserChatGrain>(chatId);

        await Task.WhenAll(
            chatGrain.UpdateChatForAsync(
                userId: me,
                peerId: fromUserId,
                previewText: null,
                timestamp: ts,
                ct),

            chatGrain.UpdateChatForAsync(
                userId: fromUserId,
                peerId: me,
                previewText: null,
                timestamp: ts,
                ct)
        );

        logger.LogInformation("Successfully accepted friend request {User}<->{From}", me, fromUserId);
    }

    public async Task DeclineFriendRequestAsync(Guid fromUserId, CancellationToken ct = default)
    {
        var me = this.GetUserId();
        logger.LogInformation("Declining friend request: {From}->{User}", fromUserId, me);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var request = await ctx.FriendRequest
           .FirstOrDefaultAsync(x => x.RequesterId == fromUserId && x.TargetId == me, ct);

        if (request is null)
        {
            logger.LogWarning("No pending friend request to decline: {From}->{User}", fromUserId, me);
            return;
        }

        ctx.FriendRequest.Remove(request);
        await ctx.SaveChangesAsync(ct);

        await NotifyAsync(fromUserId,
            new FriendRequestDeclinedEvent(me));

        logger.LogInformation("Successfully declined friend request {From}->{User}", fromUserId, me);
    }


    public async Task CancelFriendRequestAsync(Guid toUserId, CancellationToken ct = default)
    {
        var me = this.GetUserId();
        logger.LogInformation("User {User} cancelling outgoing request to {To}", me, toUserId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var request = await ctx.FriendRequest
           .FirstOrDefaultAsync(x => x.RequesterId == me && x.TargetId == toUserId, ct);

        if (request is null)
        {
            logger.LogWarning("No outgoing friend request to {To} found for cancel", toUserId);
            return;
        }

        ctx.FriendRequest.Remove(request);
        await ctx.SaveChangesAsync(ct);

        await NotifyAsync(toUserId,
            new FriendRequestCanceledEvent(me));

        logger.LogInformation("Canceled outgoing friend request {User}->{To}", me, toUserId);
    }

    public async Task BlockUserAsync(Guid userId, CancellationToken ct = default)
    {
        var meUserId = this.GetUserId();
        if (userId == meUserId)
        {
            logger.LogWarning("User {UserId} attempted to block themselves", meUserId);
            return;
        }

        logger.LogInformation("Blocking user: {User} -> {Blocked}", meUserId, userId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        try
        {
            var strategy = ctx.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async token =>
            {
                ctx.ChangeTracker.Clear();
                await using var tx = await ctx.Database.BeginTransactionAsync(token);


                logger.LogDebug("Deleting friendships {UserId}<->{FriendId}", meUserId, userId);

                var frRows = await Between(ctx.Friends.IgnoreQueryFilters(), meUserId, userId).ExecuteDeleteAsync(token);

                logger.LogInformation("Deleted {Count} friendship rows during block", frRows);

                logger.LogDebug("Deleting friend requests between {User} and {Target}", meUserId, userId);

                var reqRows = await ctx.FriendRequest
                   .IgnoreQueryFilters()
                   .Where(r => (r.RequesterId == meUserId && r.TargetId == userId) || (r.RequesterId == userId && r.TargetId == meUserId))
                   .ExecuteDeleteAsync(token);

                logger.LogInformation("Deleted {Count} friend request rows during block", reqRows);

                var exists = await ctx.UserBlocklist
                   .AnyAsync(x => x.UserId == meUserId && x.BlockedId == userId, token);

                if (exists)
                {
                    logger.LogInformation("Block already exists: {User} -> {Blocked}", meUserId, userId);
                }
                else
                {
                    logger.LogInformation("Inserting new block: {User} -> {Blocked}", meUserId, userId);

                    ctx.UserBlocklist.Add(new UserBlockEntity
                    {
                        UserId    = meUserId,
                        BlockedId = userId,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                    await ctx.SaveChangesAsync(token);

                    logger.LogInformation("Successfully blocked user: {User} -> {Blocked}", meUserId, userId);
                }

                await tx.CommitAsync(token);
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to block user {User} -> {Blocked}", meUserId, userId);
            throw;
        }

        await NotifyAsync(meUserId, new UserBlockedEvent(userId));
    }

    public async Task UnblockUserAsync(Guid userId, CancellationToken ct = default)
    {
        var meUserId = this.GetUserId();

        if (meUserId == userId)
        {
            logger.LogWarning("User {UserId} attempted to unblock themselves", meUserId);
            return;
        }

        logger.LogInformation("Unblocking user: {User} -> {Blocked}", meUserId, userId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        var block = await ctx.UserBlocklist
           .FirstOrDefaultAsync(x => x.UserId == meUserId && x.BlockedId == userId, ct);

        if (block is null)
        {
            logger.LogInformation("Unblock requested, but block record does not exist: {User}->{Blocked}", meUserId, userId);
            return;
        }

        ctx.UserBlocklist.Remove(block);
        await ctx.SaveChangesAsync(ct);

        await NotifyAsync(meUserId, new UserUnblockedEvent(userId));

        logger.LogInformation("Successfully unblocked user: {User} -> {Blocked}", meUserId, userId);
    }

    public async Task<List<UserIgnore>> GetIgnoreListAsync(int limit, int offset, CancellationToken ct = default)
    {
        var             meUserId = this.GetUserId();
        await using var ctx      = await context.CreateDbContextAsync(ct);

        var result = await ctx.UserIgnorelist
           .AsNoTracking()
           .Where(x => x.UserId == meUserId)
           .OrderByDescending(x => x.CreatedAt)
           .Skip(offset)
           .Take(limit)
           .ToListAsync(ct);
        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task IgnoreUserAsync(Guid userId, CancellationToken ct = default)
    {
        var meUserId = this.GetUserId();
        if (userId == meUserId)
        {
            logger.LogWarning("User {UserId} attempted to ignore themselves", meUserId);
            return;
        }

        await using var ctx = await context.CreateDbContextAsync(ct);

        var exists = await ctx.UserIgnorelist
           .AnyAsync(x => x.UserId == meUserId && x.IgnoredId == userId, ct);

        if (!exists)
        {
            ctx.UserIgnorelist.Add(new UserIgnoreEntity
            {
                UserId    = meUserId,
                IgnoredId = userId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await ctx.SaveChangesAsync(ct);
        }

        // Sent even when the row already existed, so a second window that missed the first
        // request still ends up agreeing with this one.
        await NotifyAsync(meUserId, new UserIgnoredEvent(userId));

        logger.LogInformation("Ignoring user: {User} -> {Ignored}", meUserId, userId);
    }

    public async Task UnignoreUserAsync(Guid userId, CancellationToken ct = default)
    {
        var meUserId = this.GetUserId();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserIgnorelist
           .FirstOrDefaultAsync(x => x.UserId == meUserId && x.IgnoredId == userId, ct);

        if (row is not null)
        {
            ctx.UserIgnorelist.Remove(row);
            await ctx.SaveChangesAsync(ct);
        }

        await NotifyAsync(meUserId, new UserUnignoredEvent(userId));

        logger.LogInformation("Stopped ignoring user: {User} -> {Ignored}", meUserId, userId);
    }

    private async static Task<Guid?> FindUserByUsernameAsync(ApplicationDbContext ctx, string username, CancellationToken ct)
    {
        var normalized = username.ToLowerInvariant();
        var result = await ctx.Users
           .Where(u => u.NormalizedUsername == normalized)
           .Select(u => u.Id)
           .FirstOrDefaultAsync(ct);
        if (result == Guid.Empty)
            return null;
        return result;
    }


    private static IQueryable<FriendshipEntity> Between(IQueryable<FriendshipEntity> friends, Guid a, Guid b)
        => friends.Where(f => (f.UserId == a && f.FriendId == b) || (f.UserId == b && f.FriendId == a));
}