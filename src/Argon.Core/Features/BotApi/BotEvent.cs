namespace Argon.Features.BotApi;

using Newtonsoft.Json.Serialization;

/// <summary>
/// Bot event types dispatched via SSE.
/// New event types are added here and paired with a <see cref="BotEventDefinitionAttribute"/>-decorated payload record.
/// </summary>
public enum BotEventType
{
    // Connection lifecycle
    Ready,
    Heartbeat,
    Resumed,

    // Domain events
    MessageCreate,
    MessageEdit,
    MemberJoin,
    MemberLeave,
    MemberUpdate,
    ChannelCreate,
    ChannelDelete,
    PresenceUpdate,
    VoiceJoin,
    VoiceLeave,
    CallIncoming,
    CallEnded,
    CommandInteraction,
    BotInstallingToSpace,
    BotUninstallingFromSpace,
    BotEntitlementsUpdated,
    ControlInteraction,
    SelectInteraction,
    ModalSubmit,
    TypingStart,
    TypingStop,
    ArchetypeCreate,
    ArchetypeUpdate,
    ReactionAdd,
    ReactionRemove,
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
        return base.CreateProperties(type, memberSerialization);
    }
}
