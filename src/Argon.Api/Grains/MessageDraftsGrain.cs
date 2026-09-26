namespace Argon.Grains;

using Argon.Core.Services;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// A user's composer drafts, one per channel. Keyed by the user, so one activation orders that
/// user's saves and the last one wins.
/// </summary>
public class MessageDraftsGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IEntitlementChecker entitlementChecker,
    IOptions<MessagesOptions> messageOptions) : Grain, IMessageDraftsGrain
{
    public const int MaxEntities = 512;

    public async Task SaveDraftAsync(Guid spaceId, Guid channelId, string text, List<IMessageEntity> entities)
    {
        var userId = CallerIsOwner();
        text     ??= "";
        entities ??= [];

        if (text.Length > messageOptions.Value.MaxTextLength)
            throw new InvalidOperationException($"Draft text is longer than {messageOptions.Value.MaxTextLength} characters");
        if (entities.Count > MaxEntities)
            throw new InvalidOperationException($"A draft carries at most {MaxEntities} entities");

        await using var ctx = await context.CreateDbContextAsync();

        if (string.IsNullOrWhiteSpace(text))
        {
            await ctx.MessageDrafts
               .Where(d => d.UserId == userId && d.ChannelId == channelId)
               .ExecuteDeleteAsync();
            return;
        }

        if (!await entitlementChecker.HasChannelAccessAsync(spaceId, channelId, userId, ArgonEntitlement.ViewChannel))
            throw new UnauthorizedAccessException("No access to this channel");

        var draft = await ctx.MessageDrafts.FirstOrDefaultAsync(d => d.UserId == userId && d.ChannelId == channelId);
        if (draft is null)
        {
            ctx.MessageDrafts.Add(new MessageDraftEntity
            {
                UserId    = userId,
                ChannelId = channelId,
                SpaceId   = spaceId,
                Text      = text,
                Entities  = entities,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            draft.Text      = text;
            draft.Entities  = entities;
            draft.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await ctx.SaveChangesAsync();
    }

    public async Task<MessageDraft?> GetDraftAsync(Guid spaceId, Guid channelId)
    {
        var userId = CallerIsOwner();

        await using var ctx = await context.CreateDbContextAsync();

        var draft = await ctx.MessageDrafts.AsNoTracking()
           .FirstOrDefaultAsync(d => d.UserId == userId && d.ChannelId == channelId && d.SpaceId == spaceId);

        return draft?.ToDto();
    }

    private Guid CallerIsOwner()
    {
        var userId = this.GetUserId();
        if (userId != this.GetPrimaryKey())
            throw new UnauthorizedAccessException("Drafts belong to their author");
        return userId;
    }
}
