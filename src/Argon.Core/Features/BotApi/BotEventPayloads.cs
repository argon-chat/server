namespace Argon.Features.BotApi;

/// <summary>
/// Dedicated Bot API event payload types.
/// These define the clean API surface exposed to bots — no internal file IDs or implementation details.
/// When the raw IArgonEvent type contains fields that should not be exposed (avatarFileId, iconFileId, etc.),
/// a dedicated payload type is used instead.
/// </summary>

// --- Member Events ---

/// <summary>Payload for <see cref="BotEventType.MemberUpdate"/>.</summary>
public sealed record BotMemberUpdatePayload(Guid SpaceId, BotMemberInfo Member);

/// <summary>Member info exposed to bots (no avatarFileId).</summary>
public sealed record BotMemberInfo(Guid UserId, string Username, string DisplayName);


// --- Archetype (Role) Events ---

/// <summary>Payload for <see cref="BotEventType.ArchetypeChanged"/> and <see cref="BotEventType.ArchetypeCreated"/>.</summary>
public sealed record BotArchetypePayload(Guid SpaceId, BotArchetypeInfo Archetype);

/// <summary>Archetype info exposed to bots (no iconFileId).</summary>
public sealed record BotArchetypeInfo(
    Guid    Id,
    Guid    SpaceId,
    string  Name,
    string  Description,
    bool    IsMentionable,
    int     Colour,
    bool    IsHidden,
    bool    IsLocked,
    bool    IsGroup,
    bool    IsDefault);


// --- Space Events ---

/// <summary>Payload for <see cref="BotEventType.SpaceModified"/> (SpaceDetailsUpdated variant).</summary>
public sealed record BotSpaceDetailsPayload(Guid SpaceId, BotSpaceInfo Details);

/// <summary>Space info exposed to bots (no avatarFieldId, topBannerFileId).</summary>
public sealed record BotSpaceInfo(Guid SpaceId, string Name, string Description);
