namespace Argon.Grains;

using System.Linq.Expressions;
using Argon.Core.Grains.Interfaces;
using Core.Entities.Data;
using Orleans.Concurrency;

[StatelessWorker]
public class FriendsGrain(IDbContextFactory<ApplicationDbContext> context, ILogger<IFriendsGrain> logger) : Grain, IFriendsGrain
{
    public async Task<List<UserBlock>> GetBlockListAsync(int limit, int offset, CancellationToken ct = default)
    {
        var             meUserId = this.GetUserId();
        await using var ctx      = await context.CreateDbContextAsync(ct);

        var result = await ctx.UserBlocklist
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

            await ExecuteInTransactionAsync(ctx, async () =>
            {
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
            }, ct);

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
            ExpiredAt = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7))
        });

        await ctx.SaveChangesAsync(ct);
        return SendFriendStatus.SuccessSent;
    }


    public async Task RemoveFriendAsync(Guid userId, CancellationToken ct = default)
    {
        var meUserId = this.GetUserId();
        logger.LogInformation("Removing friend: {User} -> {Friend}", meUserId, userId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        const string table = FriendshipEntity.TableName;

        var colUserId   = Q<FriendshipEntity>(x => x.UserId);
        var colFriendId = Q<FriendshipEntity>(x => x.FriendId);

        var sql =
            $"""
             delete from {table}
             where ({colUserId} = @p0 and {colFriendId} = @p1)
                or ({colUserId} = @p1 and {colFriendId} = @p0)
             """;

        var affected = await ctx.Database.ExecuteSqlRawAsync(sql, [
            meUserId, userId
        ], ct);

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

        await ExecuteInTransactionAsync(ctx, async () =>
        {
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
        }, ct);

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

        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await ctx.Database.BeginTransactionAsync(ct);

            try
            {
                const string friendshipTable = FriendshipEntity.TableName;
                const string reqTable        = FriendRequestEntity.TableName;

                var fUserId   = Q<FriendshipEntity>(x => x.UserId);
                var fFriendId = Q<FriendshipEntity>(x => x.FriendId);

                var rReqId = Q<FriendRequestEntity>(x => x.RequesterId);
                var rTarId = Q<FriendRequestEntity>(x => x.TargetId);

                logger.LogDebug("Deleting friendships {UserId}<->{FriendId}", meUserId, userId);

                var sqlDeleteFriendship =
                    $"""
                     delete from {friendshipTable}
                     where ({fUserId} = @p0 and {fFriendId} = @p1)
                        or ({fUserId} = @p1 and {fFriendId} = @p0)
                     """;

                var frRows = await ctx.Database.ExecuteSqlRawAsync(sqlDeleteFriendship, [
                    meUserId, userId
                ], ct);

                logger.LogInformation("Deleted {Count} friendship rows during block", frRows);

                logger.LogDebug("Deleting friend requests between {User} and {Target}", meUserId, userId);

                var sqlDeleteRequests =
                    $"""
                     delete from {reqTable}
                     where ({rReqId} = @p0 and {rTarId} = @p1)
                        or ({rReqId} = @p1 and {rTarId} = @p0)
                     """;

                var reqRows = await ctx.Database.ExecuteSqlRawAsync(sqlDeleteRequests, [
                    meUserId, userId
                ], ct);

                logger.LogInformation("Deleted {Count} friend request rows during block", reqRows);

                var exists = await ctx.UserBlocklist
                   .AnyAsync(x => x.UserId == meUserId && x.BlockedId == userId, ct);

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
                    await ctx.SaveChangesAsync(ct);

                    logger.LogInformation("Successfully blocked user: {User} -> {Blocked}", meUserId, userId);
                }

                await tx.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to block user {User} -> {Blocked}", meUserId, userId);
                throw;
            }
        });
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

        logger.LogInformation("Successfully unblocked user: {User} -> {Blocked}", meUserId, userId);
    }

    private async static Task ExecuteInTransactionAsync(
        ApplicationDbContext ctx,
        Func<Task> action,
        CancellationToken ct)
    {
        var strategy = ctx.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
            await action();
            await transaction.CommitAsync(ct);
        });
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


    private static string Q<T>(Expression<Func<T, object?>> expr)
    {
        var name = ExtractMemberName(expr.Body);
        return $"\"{name}\"";
    }

    private static string ExtractMemberName(Expression expr)
    {
        expr = expr is UnaryExpression { NodeType: ExpressionType.Convert } u
            ? u.Operand
            : expr;
        return expr switch
        {
            MemberExpression m => m.Member.Name,
            _                  => throw new NotSupportedException($"Unsupported expression: {expr}")
        };
    }
}