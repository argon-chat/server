namespace Argon.Features.BotApi;

/// <summary>
/// Converts internal Ion-generated types to public Bot API DTOs.
/// </summary>
public static class BotEventMapper
{
    public static BotChannelFullV1 FromArgonChannel(ArgonChannel ch)
        => new(ch.channelId, ch.spaceId, (BotChannelType)(int)ch.type, ch.name, ch.description);

    // Bot API V1 has no Snooze: to a bot a sleeping user is Away.
    public static BotUserStatus FromUserStatus(UserStatus s)
        => s is UserStatus.Snooze ? BotUserStatus.Away : (BotUserStatus)(int)s;

    public static BotActivityV1 FromActivityPresence(UserActivityPresence p)
        => new((BotActivityKind)(int)p.kind, (uint)p.startTimestampSeconds, p.titleName);

    public static BotArchetypeV1 FromArchetype(Archetype a, bool includePermissions = false)
        => new(a.id, a.spaceId, a.name, a.colour, a.isMentionable, a.isDefault,
            includePermissions ? (long)a.entitlement : null);

    public static BotMessageEntityV1? MapEntity(IMessageEntity entity) => entity switch
    {
        MessageEntityBold e             => Base(e.type, e.offset, e.length),
        MessageEntityItalic e           => Base(e.type, e.offset, e.length),
        MessageEntityStrikethrough e    => Base(e.type, e.offset, e.length),
        MessageEntitySpoiler e          => Base(e.type, e.offset, e.length),
        MessageEntityMonospace e        => Base(e.type, e.offset, e.length),
        MessageEntityOrdinal e          => Base(e.type, e.offset, e.length),
        MessageEntityCapitalized e      => Base(e.type, e.offset, e.length),
        MessageEntityMentionEveryone e  => Base(e.type, e.offset, e.length),
        MessageEntityFraction e         => Base(e.type, e.offset, e.length) with { Numerator = e.numerator, Denominator = e.denominator },
        MessageEntityMention e          => Base(e.type, e.offset, e.length) with { UserId = e.userId },
        MessageEntityMentionRole e      => Base(e.type, e.offset, e.length) with { ArchetypeId = e.archetypeId },
        MessageEntityEmail e            => Base(e.type, e.offset, e.length) with { Email = e.email },
        MessageEntityHashTag e          => Base(e.type, e.offset, e.length) with { Hashtag = e.hashtag },
        MessageEntityQuote e            => Base(e.type, e.offset, e.length) with { QuotedUserId = e.quotedUserId },
        MessageEntityUnderline e        => Base(e.type, e.offset, e.length) with { Colour = e.colour },
        MessageEntityUrl e              => Base(e.type, e.offset, e.length) with { Domain = e.domain, Path = e.path },
        MessageEntitySystemCallStarted e  => Base(e.type, e.offset, e.length) with { CallerId = e.callerId, CallId = e.callId },
        MessageEntitySystemCallEnded e    => Base(e.type, e.offset, e.length) with { CallerId = e.callerId, CallId = e.callId, DurationSeconds = e.durationSeconds },
        MessageEntitySystemCallTimeout e  => Base(e.type, e.offset, e.length) with { CallerId = e.callerId, CallId = e.callId },
        MessageEntitySystemUserJoined e   => Base(e.type, e.offset, e.length) with { UserId = e.userId, InviterId = e.inviterId },
        MessageEntityAttachment e         => Base(e.type, e.offset, e.length) with { FileName = e.fileName, FileSize = e.fileSize, ContentType = e.contentType, Width = e.width, Height = e.height, ThumbHash = e.thumbHash, Url = e.downloadUrl },
        MessageEntityGif e               => Base(e.type, e.offset, e.length) with { Width = e.width, Height = e.height },
        MessageEntityLinkPreview e       => Base(e.type, e.offset, e.length) with { Url = e.url, Title = e.title, Description = e.description, SiteName = e.siteName, ImageUrl = e.imageUrl, CanonicalUrl = e.canonicalUrl },
        // V1 has no video shape: a bot sees one as the attachment it would have been.
        MessageEntityVideo e             => Base(EntityType.Attachment, e.offset, e.length) with { FileName = e.fileName, FileSize = e.fileSize, ContentType = e.contentType, Width = e.width, Height = e.height, ThumbHash = e.thumbHash, Url = e.downloadUrl },
        // Stickers and custom emoji have no V1 shape; bots do not see them.
        _ => null
    };

    public static async ValueTask<BotMessageV1> FromArgonMessageAsync(ArgonMessage msg, BotUserCache userCache, List<ControlRowV1>? controls = null)
    {
        var entities = msg.entities.Values
           .Where(e => e is not null)
           .Select(e => MapEntity(e!))
           .OfType<BotMessageEntityV1>()
           .ToList();

        var sender = await userCache.GetOrResolveAsync(msg.sender);

        var reactions = msg.reactions.Size > 0
            ? msg.reactions.Values
               .Select(r => new BotReactionV1(r.emoji, r.count, r.userIds.Values.ToList(), r.customEmojiId))
               .ToList()
            : null;

        var attachments = AttachmentsOf(msg.entities.Values);

        var crosspost = msg.crosspost is { } cp
            ? new BotCrosspostV1(cp.sourceSpaceId, cp.sourceChannelId, cp.sourceMessageId, cp.sourceSpaceName, cp.sourceChannelName)
            : null;

        var webhook = msg.webhook is { } hook ? new BotWebhookV1(hook.webhookId, hook.name, hook.avatarFileId) : null;

        return new BotMessageV1(
            msg.messageId, msg.replyId, msg.channelId, msg.spaceId,
            msg.text, entities, msg.timeSent.UtcDateTime, sender, controls, reactions, crosspost, webhook,
            attachments.Count > 0 ? attachments : null);
    }

    /// <summary>The files a message carries, videos among them as the attachments they were before V1 knew of videos.</summary>
    public static List<BotAttachmentV1> AttachmentsOf(IEnumerable<IMessageEntity?> entities)
        => entities.Select(e => e switch
            {
                MessageEntityAttachment a => FromAttachment(a),
                MessageEntityVideo v      => FromVideo(v),
                _                         => null
            })
           .OfType<BotAttachmentV1>()
           .ToList();

    public static BotAttachmentV1 FromVideo(MessageEntityVideo v)
        => new(v.fileId, v.downloadUrl ?? string.Empty, v.fileName, v.fileSize, v.contentType, v.width, v.height);

    public static BotAttachmentV1 FromAttachment(MessageEntityAttachment a)
        => new(a.fileId, a.downloadUrl ?? string.Empty, a.fileName, a.fileSize, a.contentType, a.width, a.height);

    public static BotExpressionItemV1 FromExpressionItem(ExpressionItem i, bool createdByBot)
        => new(i.itemId, i.packId, i.spaceId, (BotExpressionKind)(int)i.kind, (BotExpressionFormat)(int)i.format, i.name,
            i.downloadUrl, i.width, i.height, i.fileSize, i.emoji.Values?.ToList() ?? [], i.keywords.Values?.ToList() ?? [],
            i.textColor, i.sortOrder, i.creatorId, createdByBot);

    public static BotExpressionPackV1 FromExpressionPack(ExpressionPack p, Func<Guid?, bool> byBot)
        => new(p.packId, p.spaceId, (BotExpressionKind)(int)p.kind, p.title, p.slug, p.coverItemId, p.sortOrder, p.version,
            p.items.Values?.Select(i => FromExpressionItem(i, byBot(i.creatorId))).ToList() ?? [], p.creatorId, byBot(p.creatorId));

    /// <summary>Which of the creators are bot accounts, by the BOT flag the clients show.</summary>
    public static async ValueTask<Func<Guid?, bool>> BotCreatorsAsync(IEnumerable<Guid?> creatorIds, BotUserCache users)
    {
        var bots = new HashSet<Guid>();

        foreach (var id in creatorIds.OfType<Guid>().Distinct())
        {
            try
            {
                if ((await users.GetOrResolveAsync(id)).Flags.HasFlag(UserFlag.BOT))
                    bots.Add(id);
            }
            catch (Exception)
            {
                // An account that cannot be read is not reported as a bot.
            }
        }

        return id => id is { } creator && bots.Contains(creator);
    }

    public static async ValueTask<List<BotExpressionPackV1>> FromExpressionPacksAsync(IEnumerable<ExpressionPack> packs, BotUserCache users)
    {
        var list  = packs.ToList();
        var byBot = await BotCreatorsAsync(list.SelectMany(p => (p.items.Values ?? []).Select(i => i.creatorId).Append(p.creatorId)), users);
        return list.Select(p => FromExpressionPack(p, byBot)).ToList();
    }

    /// <summary>Null for a change V1 has no shape for; the bot re-reads the list then.</summary>
    public static async ValueTask<BotExpressionsDeltaV1?> FromExpressionDeltaAsync(IExpressionDelta? delta, BotUserCache users)
    {
        switch (delta)
        {
            case PackUpserted d:
                return new BotExpressionsDeltaV1("packUpserted", Pack: (await FromExpressionPacksAsync([d.pack], users))[0], PackId: d.pack.packId);

            case PackDeleted d:
                return new BotExpressionsDeltaV1("packDeleted", PackId: d.packId);

            case ItemUpserted d:
            {
                var byBot = await BotCreatorsAsync([d.item.creatorId], users);
                return new BotExpressionsDeltaV1("itemUpserted", Item: FromExpressionItem(d.item, byBot(d.item.creatorId)),
                    PackId: d.item.packId, ItemId: d.item.itemId);
            }

            case ItemDeleted d:
                return new BotExpressionsDeltaV1("itemDeleted", PackId: d.packId, ItemId: d.itemId);

            case PacksReordered d:
                return new BotExpressionsDeltaV1("packsReordered", Kind: (BotExpressionKind)(int)d.kind, Ordered: d.ordered.Values?.ToList() ?? []);

            case ItemsReordered d:
                return new BotExpressionsDeltaV1("itemsReordered", PackId: d.packId, Ordered: d.ordered.Values?.ToList() ?? []);

            default:
                return null;
        }
    }

    private static BotMessageEntityV1 Base(EntityType type, int offset, int length) => new()
    {
        Type   = (BotEntityType)(int)type,
        Offset = offset,
        Length = length
    };
}