namespace Argon.Api.Features.CoreLogic.Social;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Whether one person has any standing reason to know another.
/// </summary>
/// <remarks>
/// <para>The anchors, cheapest first, each a relationship the target took part in: a block in
/// either direction (which is a refusal, and wins), being friends, a request either way, a
/// conversation that exists, or a space in common. A pending request counts because the target
/// has to be able to see who is asking.</para>
///
/// <para>One definition, used by profile lookup and by the report system. A bare user id must not
/// be enough to walk the directory, and it must not be enough to file a report against a stranger
/// either — the second is how a list of ids becomes a list of victims.</para>
/// </remarks>
public static class SocialReach
{
    public static async Task<bool> CanReachAsync(ApplicationDbContext ctx, Guid callerId, Guid targetId, CancellationToken ct = default)
    {
        if (callerId == targetId)
            return true;

        if (await ctx.UserBlocklist.AnyAsync(
                x => (x.UserId == callerId && x.BlockedId == targetId) ||
                     (x.UserId == targetId && x.BlockedId == callerId), ct))
            return false;

        if (await ctx.Friends.AnyAsync(
                x => (x.UserId == callerId && x.FriendId == targetId) ||
                     (x.UserId == targetId && x.FriendId == callerId), ct))
            return true;

        if (await ctx.FriendRequest.AnyAsync(
                x => (x.RequesterId == callerId && x.TargetId == targetId) ||
                     (x.RequesterId == targetId && x.TargetId == callerId), ct))
            return true;

        var conversationId = ConversationEntity.GenerateConversationId(callerId, targetId);

        if (await ctx.Conversations.AnyAsync(x => x.Id == conversationId, ct))
            return true;

        var callerSpaces = ctx.UsersToServerRelations
           .Where(x => x.UserId == callerId)
           .Select(x => x.SpaceId);

        return await ctx.UsersToServerRelations
           .AnyAsync(x => x.UserId == targetId && callerSpaces.Contains(x.SpaceId), ct);
    }
}
