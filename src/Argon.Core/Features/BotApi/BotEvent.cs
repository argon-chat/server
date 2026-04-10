namespace Argon.Features.BotApi;

using Newtonsoft.Json.Serialization;

/// <summary>
/// All bot event types dispatched via SSE.
/// </summary>
public enum BotEventType
{
    // Connection lifecycle
    Ready,
    Heartbeat,
    Resumed,

    // Messages (Intent: Messages)
    MessageCreate,
    MessageUpdate,
    MessageDelete,

    // Members (Intent: Members — privileged)
    MemberJoin,
    MemberLeave,
    MemberUpdate,

    // Channels (Intent: Channels)
    ChannelCreate,
    ChannelUpdate,
    ChannelDelete,

    // Typing (Intent: Typing)
    TypingStart,
    TypingStop,

    // Presence (Intent: Presence — privileged)
    PresenceUpdate,

    // Commands (Intent: Commands)
    CommandInteraction,

    // Direct Messages (Intent: DirectMessages)
    DirectMessageCreate,

    // Moderation (Intent: Moderation)
    MemberKicked,
    MemberBanned,

    // Archetypes (Intent: Archetypes)
    ArchetypeChanged,
    ArchetypeCreated,

    // Space Updates (Intent: SpaceUpdates)
    SpaceModified,

    // Voice (Intent: Voice)
    VoiceJoin,
    VoiceLeave,
    VoiceMute,
    VoiceUnmute,
    VoiceStreamStart,
    VoiceStreamStop,

    // Calls (Intent: Calls — privileged)
    CallIncoming,
    CallEnded,
}

/// <summary>
/// An SSE event dispatched to a bot. Serialized as JSON in the SSE data field.
/// </summary>
public sealed record BotSseEvent
{
    public required string       Id        { get; init; }
    public required BotEventType Type      { get; init; }
    public          Guid?        SpaceId   { get; init; }
    public          Guid?        ChannelId { get; init; }
    public required object       Data      { get; init; }
}

/// <summary>
/// Contract resolver for SSE JSON output:
/// - camelCase property names
/// - Excludes Ion union internals (UnionKey, UnionIndex)
/// - Serializes IMessageEntity as concrete type (all properties visible)
/// </summary>
public sealed class BotSseContractResolver : Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver
{
    private static readonly HashSet<string> ExcludedProperties = ["UnionKey", "UnionIndex"];

    protected override JsonProperty CreateProperty(System.Reflection.MemberInfo member,
        Newtonsoft.Json.MemberSerialization memberSerialization)
    {
        var prop = base.CreateProperty(member, memberSerialization);
        if (ExcludedProperties.Contains(member.Name))
            prop.ShouldSerialize = _ => false;
        return prop;
    }

    protected override IList<JsonProperty> CreateProperties(Type type,
        Newtonsoft.Json.MemberSerialization memberSerialization)
    {
        // For interface types like IMessageEntity, resolve properties from the runtime type
        // This is handled by Newtonsoft when TypeNameHandling is off — the serializer uses
        // the actual object type. We just need to make sure our excludes work.
        return base.CreateProperties(type, memberSerialization);
    }
}

/// <summary>
/// Maps IArgonEvent types to BotEventType and their required BotIntent.
/// </summary>
public static class BotEventMapping
{
    internal static readonly Dictionary<string, (BotEventType EventType, BotIntent RequiredIntent)> Map = new()
    {
        // Messages
        ["MessageSent"]    = (BotEventType.MessageCreate, BotIntent.Messages),

        // Members
        ["JoinToServerUser"]       = (BotEventType.MemberJoin, BotIntent.Members),
        ["UserUpdated"]            = (BotEventType.MemberUpdate, BotIntent.Members),

        // Voice
        ["JoinedToChannelUser"]    = (BotEventType.VoiceJoin, BotIntent.Voice),
        ["LeavedFromChannelUser"]  = (BotEventType.VoiceLeave, BotIntent.Voice),

        // Channels
        ["ChannelCreated"]  = (BotEventType.ChannelCreate, BotIntent.Channels),
        ["ChannelModified"] = (BotEventType.ChannelUpdate, BotIntent.Channels),
        ["ChannelRemoved"]  = (BotEventType.ChannelDelete, BotIntent.Channels),

        // Typing
        ["UserTypingEvent"]     = (BotEventType.TypingStart, BotIntent.Typing),
        ["UserStopTypingEvent"] = (BotEventType.TypingStop, BotIntent.Typing),

        // Presence
        ["UserChangedStatus"]                = (BotEventType.PresenceUpdate, BotIntent.Presence),
        ["OnUserPresenceActivityChanged"]    = (BotEventType.PresenceUpdate, BotIntent.Presence),
        ["OnUserPresenceActivityRemoved"]    = (BotEventType.PresenceUpdate, BotIntent.Presence),

        // Archetypes
        ["ArchetypeChanged"] = (BotEventType.ArchetypeChanged, BotIntent.Archetypes),
        ["ArchetypeCreated"] = (BotEventType.ArchetypeCreated, BotIntent.Archetypes),

        // Space
        ["ServerModified"]        = (BotEventType.SpaceModified, BotIntent.SpaceUpdates),
        ["SpaceDetailsUpdated"]   = (BotEventType.SpaceModified, BotIntent.SpaceUpdates),

        // DMs
        ["DirectMessageSent"] = (BotEventType.DirectMessageCreate, BotIntent.DirectMessages),

        // Calls
        ["CallIncoming"] = (BotEventType.CallIncoming, BotIntent.Calls),
        ["CallFinished"] = (BotEventType.CallEnded, BotIntent.Calls),
    };

    /// <summary>
    /// Overrides the IArgonEvent type with a dedicated Bot API payload type for documentation and serialization.
    /// When present, the clean payload type is used instead of the raw internal type.
    /// </summary>
    internal static readonly Dictionary<string, Type> PayloadOverrides = new()
    {
        ["UserUpdated"]        = typeof(BotMemberUpdatePayload),
        ["ArchetypeChanged"]   = typeof(BotArchetypePayload),
        ["ArchetypeCreated"]   = typeof(BotArchetypePayload),
        ["SpaceDetailsUpdated"] = typeof(BotSpaceDetailsPayload),
    };

    public static (BotEventType EventType, BotIntent RequiredIntent)? TryMap(string unionKey)
        => Map.TryGetValue(unionKey, out var result) ? result : null;

    /// <summary>
    /// Get the required intent for a given event type. Returns null for lifecycle events.
    /// </summary>
    public static BotIntent? GetRequiredIntent(BotEventType eventType)
        => EventIntents.GetValueOrDefault(eventType);
    /// <summary>
    /// Short descriptions for each event type, used in documentation generation.
    /// </summary>
    internal static readonly Dictionary<BotEventType, string> Descriptions = new()
    {
        [BotEventType.Ready]                = "Sent on connection. Contains active intents and space IDs.",
        [BotEventType.Heartbeat]            = "Keep-alive ping, sent every 30 seconds.",
        [BotEventType.Resumed]              = "Sent after replaying missed events on reconnection.",
        [BotEventType.MessageCreate]        = "A new message was sent in a channel.",
        [BotEventType.MessageUpdate]        = "A message was edited.",
        [BotEventType.MessageDelete]        = "A message was deleted.",
        [BotEventType.MemberJoin]           = "A user joined a space.",
        [BotEventType.MemberLeave]          = "A user left a space.",
        [BotEventType.MemberUpdate]         = "A member's profile or roles changed.",
        [BotEventType.ChannelCreate]        = "A new channel was created.",
        [BotEventType.ChannelUpdate]        = "A channel's settings were modified.",
        [BotEventType.ChannelDelete]        = "A channel was deleted.",
        [BotEventType.TypingStart]          = "A user started typing in a channel.",
        [BotEventType.TypingStop]           = "A user stopped typing.",
        [BotEventType.PresenceUpdate]       = "A user's online status or activity changed.",
        [BotEventType.CommandInteraction]   = "A user invoked a slash command registered by the bot.",
        [BotEventType.DirectMessageCreate]  = "A direct message was sent to the bot.",
        [BotEventType.MemberKicked]         = "A member was kicked from a space.",
        [BotEventType.MemberBanned]         = "A member was banned from a space.",
        [BotEventType.ArchetypeChanged]     = "An archetype (role) was modified.",
        [BotEventType.ArchetypeCreated]     = "A new archetype (role) was created.",
        [BotEventType.SpaceModified]        = "Space settings or details were updated.",
        [BotEventType.VoiceJoin]            = "A user joined a voice channel.",
        [BotEventType.VoiceLeave]           = "A user left a voice channel.",
        [BotEventType.VoiceMute]            = "A user muted themselves in a voice channel.",
        [BotEventType.VoiceUnmute]          = "A user unmuted themselves in a voice channel.",
        [BotEventType.VoiceStreamStart]     = "A user started streaming (screen share or camera).",
        [BotEventType.VoiceStreamStop]      = "A user stopped streaming.",
        [BotEventType.CallIncoming]         = "An incoming call is ringing for the bot.",
        [BotEventType.CallEnded]            = "A call the bot was involved in has ended.",
    };

    /// <summary>
    /// Canonical mapping from each event type to the intent it belongs to.
    /// Connection lifecycle events have no intent (null).
    /// </summary>
    internal static readonly Dictionary<BotEventType, BotIntent?> EventIntents = new()
    {
        [BotEventType.Ready]                = null,
        [BotEventType.Heartbeat]            = null,
        [BotEventType.Resumed]              = null,
        [BotEventType.MessageCreate]        = BotIntent.Messages,
        [BotEventType.MessageUpdate]        = BotIntent.Messages,
        [BotEventType.MessageDelete]        = BotIntent.Messages,
        [BotEventType.MemberJoin]           = BotIntent.Members,
        [BotEventType.MemberLeave]          = BotIntent.Members,
        [BotEventType.MemberUpdate]         = BotIntent.Members,
        [BotEventType.ChannelCreate]        = BotIntent.Channels,
        [BotEventType.ChannelUpdate]        = BotIntent.Channels,
        [BotEventType.ChannelDelete]        = BotIntent.Channels,
        [BotEventType.TypingStart]          = BotIntent.Typing,
        [BotEventType.TypingStop]           = BotIntent.Typing,
        [BotEventType.PresenceUpdate]       = BotIntent.Presence,
        [BotEventType.CommandInteraction]   = BotIntent.Commands,
        [BotEventType.DirectMessageCreate]  = BotIntent.DirectMessages,
        [BotEventType.MemberKicked]         = BotIntent.Moderation,
        [BotEventType.MemberBanned]         = BotIntent.Moderation,
        [BotEventType.ArchetypeChanged]     = BotIntent.Archetypes,
        [BotEventType.ArchetypeCreated]     = BotIntent.Archetypes,
        [BotEventType.SpaceModified]        = BotIntent.SpaceUpdates,
        [BotEventType.VoiceJoin]            = BotIntent.Voice,
        [BotEventType.VoiceLeave]           = BotIntent.Voice,
        [BotEventType.VoiceMute]            = BotIntent.Voice,
        [BotEventType.VoiceUnmute]          = BotIntent.Voice,
        [BotEventType.VoiceStreamStart]     = BotIntent.Voice,
        [BotEventType.VoiceStreamStop]      = BotIntent.Voice,
        [BotEventType.CallIncoming]         = BotIntent.Calls,
        [BotEventType.CallEnded]            = BotIntent.Calls,
    };
}
