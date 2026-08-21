namespace Argon.Features.BotApi;

/// <summary>
/// Registers a record as a Bot API event definition.
/// The decorated type's public properties define the event payload shape
/// used for documentation, contract hashing, and (future) serialization.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class BotEventDefinitionAttribute(BotEventType eventType, string category) : Attribute
{
    public BotEventType EventType { get; } = eventType;
    public string       Category  { get; } = category;

    /// <summary>
    /// The intent required to receive this event. <see cref="BotIntent.None"/> for lifecycle events (Ready, Heartbeat, Resumed).
    /// </summary>
    public BotIntent Intent { get; set; } = BotIntent.None;

    /// <summary>
    /// Future: the IArgonBotEvent union key that maps to this event at runtime.
    /// </summary>
    public string? InternalEventKey { get; set; }
}

/// <summary>
/// Human-readable description for a Bot API event, used in generated documentation.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BotEventDescriptionAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

/// <summary>
/// Freezes a Bot API event payload shape with a SHA-256 hash.
/// At startup, the server re-computes the hash and refuses to start if it differs,
/// preventing accidental breaking changes to published event contracts.
/// <para>
/// Use <c>dotnet run -- bot-api rehash</c> to compute hashes for all event definitions.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class StableEventContractAttribute(string contractHash) : Attribute
{
    public string ContractHash { get; } = contractHash;
}

/// <summary>
/// Marks a type as a versioned Bot API DTO. Version number should be in the type name (e.g. BotUserV1).
/// Discovered by <see cref="BotContractVerifier"/> for documentation and changelog generation.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class BotDtoVersionAttribute(int version) : Attribute
{
    public int Version { get; } = version;
}

/// <summary>
/// Links a DTO to its previous version for automatic field-level diff / changelog generation.
/// <example><c>[BotDtoPreviousVersion(typeof(BotUserV1))]</c></example>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class BotDtoPreviousVersionAttribute(Type previousVersionType) : Attribute
{
    public Type PreviousVersionType { get; } = previousVersionType;
}
