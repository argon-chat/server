namespace Argon.Features.BotApi;

/// <summary>
/// Converts internal Ion-generated types to public Bot API DTOs.
/// </summary>
public static class BotEventMapper
{
    public static BotChannelFullV1 FromArgonChannel(ArgonChannel ch)
        => new(ch.channelId, ch.spaceId, (BotChannelType)(int)ch.type, ch.name, ch.description);

    public static BotUserStatus FromUserStatus(UserStatus s)
        => (BotUserStatus)(int)s;

    public static BotActivityV1 FromActivityPresence(UserActivityPresence p)
        => new((BotActivityKind)(int)p.kind, (uint)p.startTimestampSeconds, p.titleName);

    public static BotArchetypeV1 FromArchetype(Archetype a, bool includePermissions = false)
        => new(a.id, a.spaceId, a.name, a.colour, a.isMentionable, a.isDefault,
            includePermissions ? (long)a.entitlement : null);

    public static BotMessageEntityV1 MapEntity(IMessageEntity entity) => entity switch
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
        MessageEntityAttachment e         => Base(e.type, e.offset, e.length) with { FileName = e.fileName, FileSize = e.fileSize, ContentType = e.contentType, Width = e.width, Height = e.height, ThumbHash = e.thumbHash },
        _ => Base(EntityType.Bold, 0, 0) // unreachable — all 21 variants covered
    };

    public static async ValueTask<BotMessageV1> FromArgonMessageAsync(ArgonMessage msg, BotUserCache userCache, List<ControlRowV1>? controls = null)
    {
        var entities = msg.entities.Values
           .Where(e => e is not null)
           .Select(MapEntity!)
           .ToList();

        var sender = await userCache.GetOrResolveAsync(msg.sender);

        var reactions = msg.reactions.Size > 0
            ? msg.reactions.Values
               .Select(r => new BotReactionV1(r.emoji, r.count, r.userIds.Values.ToList()))
               .ToList()
            : null;

        return new BotMessageV1(
            msg.messageId, msg.replyId, msg.channelId, msg.spaceId,
            msg.text, entities, msg.timeSent, sender, controls, reactions);
    }

    private static BotMessageEntityV1 Base(EntityType type, int offset, int length) => new()
    {
        Type   = (BotEntityType)(int)type,
        Offset = offset,
        Length = length
    };
}