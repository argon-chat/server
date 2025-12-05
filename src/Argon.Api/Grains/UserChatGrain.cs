namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Grains.Interfaces;
using Orleans.Concurrency;
using System.Linq.Expressions;
using Core.Entities.Data;

[StatelessWorker]
public class UserChatGrain(
    IDbContextFactory<ApplicationDbContext> context,
    ILogger<IUserChatGrain> logger,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier) : Grain, IUserChatGrain
{
    private Guid Me => this.GetUserId();

    public async Task<List<UserChat>> GetRecentChatsAsync(int limit, int offset, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var result = await ctx.UserChatlist
           .Where(x => x.UserId == Me)
           .OrderByDescending(x => x.IsPinned)
           .ThenByDescending(x => x.PinnedAt)
           .ThenByDescending(x => x.LastMessageAt)
           .Skip(offset)
           .Take(limit)
           .ToListAsync(ct);

        // fixation echo chat at all time top pinned
        var echoChat = result.FirstOrDefault(x => x.PeerId == UserEntity.EchoUser);

        if (echoChat is null)
        {
            result.Add(new UserChatEntity()
            {
                PeerId   = UserEntity.EchoUser,
                IsPinned = true,
                PinnedAt = DateTime.UtcNow.AddDays(900),
                UserId   = Me
            });
        }
        else
            echoChat.PinnedAt = DateTime.UtcNow.AddDays(900);

        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task PinChatAsync(Guid peerId, CancellationToken ct = default)
    {
        // not allowed pin echo
        if (peerId == UserEntity.EchoUser)
            return;

        logger.LogInformation("PinChat: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var now = DateTimeOffset.UtcNow;

            var record = await ctx.UserChatlist
               .FirstOrDefaultAsync(x => x.UserId == Me && x.PeerId == peerId, ct);

            if (record is null)
            {
                record = new UserChatEntity
                {
                    UserId          = Me,
                    PeerId          = peerId,
                    LastMessageAt   = now,
                    IsPinned        = true,
                    PinnedAt        = now,
                    LastMessageText = null,
                };
                ctx.UserChatlist.Add(record);
            }
            else
            {
                record.IsPinned = true;
                record.PinnedAt = now;
                ctx.UserChatlist.Update(record);
            }

            await ctx.SaveChangesAsync(ct);

            await NotifyAsync(Me, new ChatPinnedEvent(peerId, now.UtcDateTime));
        }, ct);
    }

    public async Task UnpinChatAsync(Guid peerId, CancellationToken ct = default)
    {
        // not allowed unpin echo
        if (peerId == UserEntity.EchoUser)
            return;

        logger.LogInformation("UnpinChat: {Me} -> {Peer}", Me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var record = await ctx.UserChatlist
               .FirstOrDefaultAsync(x => x.UserId == Me && x.PeerId == peerId, ct);

            if (record is null)
                return;

            record.IsPinned = false;
            record.PinnedAt = null;

            ctx.UserChatlist.Update(record);

            await ctx.SaveChangesAsync(ct);
        }, ct);

        await NotifyAsync(Me, new ChatUnpinnedEvent(peerId));
    }

    public async Task MarkChatReadAsync(Guid peerId, CancellationToken ct = default)
    {
        // TODO
    }

    public async Task UpdateChatAsync(Guid peerId, string? previewText, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        var me = Me;
        logger.LogDebug("UpdateChatAsync: {Me} <-> {Peer}", me, peerId);

        await using var ctx = await context.CreateDbContextAsync(ct);

        await ExecuteInTransactionAsync(ctx, async () =>
        {
            var record = await ctx.UserChatlist
               .FirstOrDefaultAsync(x => x.UserId == me && x.PeerId == peerId, ct);

            if (record is null)
            {
                record = new UserChatEntity
                {
                    UserId          = me,
                    PeerId          = peerId,
                    LastMessageAt   = timestamp,
                    LastMessageText = previewText,
                    IsPinned        = false,
                    PinnedAt        = null
                };

                ctx.UserChatlist.Add(record);
            }
            else
            {
                record.LastMessageAt   = timestamp;
                record.LastMessageText = previewText;

                ctx.UserChatlist.Update(record);
            }

            await ctx.SaveChangesAsync(ct);
        }, ct);
        //await NotifyAsync(me, new RecentChatUpdatedEvent(
        //    peerId,
            
        //    previewText,
        //    timestamp.UtcDateTime
        //));
    }


    private async Task NotifyAsync<T>(Guid userId, T payload) where T : IArgonEvent
    {
        var sessions = await sessionDiscovery.GetUserSessionsAsync(userId);

        if (sessions.Count == 0) return;

        await notifier.NotifySessionsAsync(sessions, payload);
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