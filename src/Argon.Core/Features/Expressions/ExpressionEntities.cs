namespace Argon.Features.Expressions;

/// <summary>Stickers and custom emoji in a message, held to the items the server knows.</summary>
public static class ExpressionEntities
{
    public static bool Any(IEnumerable<IMessageEntity>? entities)
        => entities?.Any(e => e is MessageEntitySticker or MessageEntityCustomEmoji) == true;

    /// <summary>The items the entities name, by the space each entity claims.</summary>
    public static Dictionary<Guid, List<Guid>> ClaimedItems(IEnumerable<IMessageEntity> entities)
    {
        var claimed = new Dictionary<Guid, List<Guid>>();

        foreach (var entity in entities)
        {
            var (spaceId, itemId) = entity switch
            {
                MessageEntitySticker s     => (s.spaceId, s.itemId),
                MessageEntityCustomEmoji c => (c.spaceId, c.itemId),
                _                          => (Guid.Empty, Guid.Empty)
            };

            if (itemId == Guid.Empty)
                continue;

            if (!claimed.TryGetValue(spaceId, out var ids))
                claimed[spaceId] = ids = [];
            if (!ids.Contains(itemId))
                ids.Add(itemId);
        }

        return claimed;
    }

    /// <summary>
    /// Keeps the stickers and custom emoji that name a live item, rewritten from it, and drops the rest.
    /// Everything else passes through untouched.
    /// </summary>
    /// <remarks>
    /// A sticker is the whole message: empty text and no other entity. A custom emoji covers exactly
    /// its <c>:name:</c> in the text. URLs are cleared; they are filled when the message is read.
    /// </remarks>
    /// <param name="spaceId">The space every item has to belong to; null holds each item to the space its entity claims.</param>
    public static List<IMessageEntity> Resolve(string? text, IReadOnlyList<IMessageEntity> entities,
        IReadOnlyDictionary<Guid, ExpressionItem> items, Guid? spaceId, int maxCustomEmoji)
    {
        text ??= string.Empty;

        var result  = new List<IMessageEntity>(entities.Count);
        var covered = new HashSet<int>();

        foreach (var entity in entities)
        {
            switch (entity)
            {
                case MessageEntitySticker sticker:
                    if (text.Length == 0 && entities.Count == 1
                     && Find(items, sticker.itemId, spaceId ?? sticker.spaceId, ExpressionKind.Sticker) is { } s)
                        result.Add(sticker with
                        {
                            type        = EntityType.Sticker,
                            offset      = 0,
                            length      = 0,
                            packId      = s.packId,
                            spaceId     = s.spaceId,
                            format      = s.format,
                            fileId      = s.fileId,
                            thumbFileId = s.thumbFileId,
                            width       = s.width,
                            height      = s.height,
                            outline     = s.outline,
                            downloadUrl = null,
                            thumbUrl    = null
                        });
                    break;

                case MessageEntityCustomEmoji emoji:
                    if (covered.Count < maxCustomEmoji
                     && Find(items, emoji.itemId, spaceId ?? emoji.spaceId, ExpressionKind.Emoji) is { } e
                     && CoversName(text, emoji.offset, emoji.length, e.name)
                     && covered.Add(emoji.offset))
                        result.Add(emoji with
                        {
                            type        = EntityType.CustomEmoji,
                            spaceId     = e.spaceId,
                            format      = e.format,
                            fileId      = e.fileId,
                            name        = e.name,
                            textColor   = e.textColor,
                            downloadUrl = null
                        });
                    break;

                default:
                    result.Add(entity);
                    break;
            }
        }

        return result;
    }

    /// <summary>Whether <c>text[offset..offset+length]</c> is exactly <c>:name:</c>.</summary>
    public static bool CoversName(string text, int offset, int length, string name)
        => offset >= 0
        && length == name.Length + 2
        && offset <= text.Length - length
        && text[offset] == ':'
        && text[offset + length - 1] == ':'
        && text.AsSpan(offset + 1, name.Length).SequenceEqual(name);

    /// <summary>An emoji entity already stored on a message, as the item it was resolved from.</summary>
    public static ExpressionItem AsItem(MessageEntityCustomEmoji e)
        => new(e.itemId, Guid.Empty, e.spaceId, ExpressionKind.Emoji, e.format, e.name, e.fileId, null, 0, 0, 0,
            ion.runtime.IonArray<string>.Empty, ion.runtime.IonArray<string>.Empty, null, e.textColor, 0, null, null);

    private static ExpressionItem? Find(IReadOnlyDictionary<Guid, ExpressionItem> items, Guid itemId, Guid spaceId, ExpressionKind kind)
        => items.TryGetValue(itemId, out var item) && item.kind == kind && item.spaceId == spaceId ? item : null;
}
