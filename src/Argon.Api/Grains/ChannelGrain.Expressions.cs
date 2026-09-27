namespace Argon.Grains;

using Argon.Features.Expressions;

public partial class ChannelGrain
{
    public async Task<IAddReactionResult> AddCustomReaction(long messageId, Guid itemId)
    {
        // A list, not a collection expression: the grain call has to be able to name the argument's type.
        var items = await LiveItemsAsync(new List<Guid> { itemId });

        // There is no "unknown emoji" refusal to give; NONE is what an unusable one reads as.
        if (!items.TryGetValue(itemId, out var item) || item.kind != ExpressionKind.Emoji || item.spaceId != SpaceId)
            return new FailedAddReaction(AddReactionError.NONE);

        return await AddReactionAsync(messageId, $":{item.name}:", itemId);
    }

    // Taking one back needs no live item: it may have been deleted since.
    public Task<IRemoveReactionResult> RemoveCustomReaction(long messageId, Guid itemId)
        => RemoveReactionAsync(messageId, r => r.CustomEmojiId == itemId);

    /// <summary>Stickers and custom emoji checked against the space's live items, and rewritten from them.</summary>
    /// <param name="stored">Custom emoji the message already carries: an edit keeps them even once their item is gone.</param>
    private async Task<List<IMessageEntity>> ResolveExpressionsAsync(string text, List<IMessageEntity> entities,
        IEnumerable<MessageEntityCustomEmoji>? stored = null)
    {
        if (!ExpressionEntities.Any(entities))
            return entities;

        var items = new Dictionary<Guid, ExpressionItem>();

        foreach (var emoji in stored ?? [])
            items[emoji.itemId] = ExpressionEntities.AsItem(emoji);

        var claimed = ExpressionEntities.ClaimedItems(entities).Values.SelectMany(ids => ids).Distinct().ToList();
        foreach (var (id, item) in await LiveItemsAsync(claimed))
            items[id] = item;

        return ExpressionEntities.Resolve(text, entities, items, SpaceId, expressionsOptions.Value.MaxCustomEmojiPerMessage);
    }

    private async Task<IReadOnlyDictionary<Guid, ExpressionItem>> LiveItemsAsync(IReadOnlyCollection<Guid> itemIds)
    {
        try
        {
            return await GrainFactory.GetGrain<ISpaceExpressionsGrain>(SpaceId).ResolveLiveItemsAsync(itemIds);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "could not resolve stickers and emoji of space {SpaceId}; treating them as unknown", SpaceId);
            return new Dictionary<Guid, ExpressionItem>();
        }
    }
}
