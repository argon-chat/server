namespace Argon.Features.Storage;

using Argon.Core.Services;

public enum SourceAccess
{
    NotFound,
    Denied,
    Allowed
}

/// <summary>
/// Who may copy a file into a chat of their own: its owner, anyone who can read the channel it was
/// posted in, or the other side of the direct chat it was sent in. A direct-chat file records its
/// conversation in <c>ChannelId</c>; one stored before that was recorded is its sender's alone.
/// </summary>
public static class AttachmentSources
{
    public static async Task<SourceAccess> CheckAsync(ApplicationDbContext db, IEntitlementChecker entitlements, Guid userId, Guid sourceFileId,
        CancellationToken ct = default)
    {
        var source = await db.Files.AsNoTracking()
           .Where(f => f.Id == sourceFileId && f.Finalized)
           .Select(f => new { f.OwnerId, f.Purpose, f.SpaceId, f.ChannelId })
           .FirstOrDefaultAsync(ct);

        if (source is null)
            return SourceAccess.NotFound;

        if (source.OwnerId == userId)
            return SourceAccess.Allowed;

        if (source is { Purpose: FilePurpose.ChannelAttachment, SpaceId: { } spaceId, ChannelId: { } channelId }
         && await entitlements.HasChannelAccessAsync(spaceId, channelId, userId, ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory, ct))
            return SourceAccess.Allowed;

        if (source is { Purpose: FilePurpose.DirectAttachment, ChannelId: { } conversationId }
         && await db.Conversations.AnyAsync(c => c.Id == conversationId && (c.Participant1Id == userId || c.Participant2Id == userId), ct))
            return SourceAccess.Allowed;

        return SourceAccess.Denied;
    }
}
