namespace Argon.Features.BotApi;

/// <summary>
/// Gateway Intents — bitmask declaring which events a bot wants to receive.
/// </summary>
[Flags]
public enum BotIntent : long
{
    None           = 0,

    /// <summary>MESSAGE_CREATE, MESSAGE_UPDATE, MESSAGE_DELETE</summary>
    Messages       = 1 << 0,

    /// <summary>MEMBER_JOIN, MEMBER_LEAVE, MEMBER_UPDATE</summary>
    Members        = 1 << 1,

    /// <summary>CHANNEL_CREATE, CHANNEL_UPDATE, CHANNEL_DELETE</summary>
    Channels       = 1 << 2,

    /// <summary>REACTION_ADD, REACTION_REMOVE</summary>
    Reactions      = 1 << 3,

    /// <summary>TYPING_START, TYPING_STOP</summary>
    Typing         = 1 << 4,

    /// <summary>PRESENCE_UPDATE (privileged)</summary>
    Presence       = 1 << 5,

    /// <summary>COMMAND_INTERACTION</summary>
    Commands       = 1 << 6,

    /// <summary>DM messages for the bot</summary>
    DirectMessages = 1 << 7,

    /// <summary>BAN, KICK events</summary>
    Moderation     = 1 << 8,

    /// <summary>ARCHETYPE_CHANGED, ARCHETYPE_CREATED</summary>
    Archetypes     = 1 << 9,

    /// <summary>SPACE_MODIFIED, SPACE_DETAILS_UPDATED</summary>
    SpaceUpdates   = 1 << 10,

    /// <summary>VOICE_JOIN, VOICE_LEAVE — voice channel participation events</summary>
    Voice          = 1 << 11,

    /// <summary>CALL_INCOMING, CALL_ENDED — incoming call events (privileged)</summary>
    Calls          = 1 << 12,

    /// <summary>CONTROL_INTERACTION — button/select interactions on messages</summary>
    ControlInteractions = 1 << 13,

    /// <summary>All non-privileged intents.</summary>
    AllNonPrivileged = Messages | Channels | Reactions | Typing | Commands | DirectMessages | Moderation | Archetypes | SpaceUpdates | Voice | ControlInteractions,

    /// <summary>Privileged intents that require bot verification.</summary>
    AllPrivileged = Presence | Members | Calls,
}
