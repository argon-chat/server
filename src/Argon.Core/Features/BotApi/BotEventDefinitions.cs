namespace Argon.Features.BotApi;

/// <summary>
/// Lifecycle event payload definitions for the Bot API.
/// Each record is decorated with <see cref="BotEventDefinitionAttribute"/>
/// and discovered automatically by <see cref="BotContractVerifier"/>.
/// </summary>

// ─── Connection Lifecycle ────────────────────────────────

[BotEventDefinition(BotEventType.Ready, "Connection")]
[BotEventDescription("Sent immediately on SSE connection. Contains active intents and subscribed space IDs.")]
[StableEventContract("af9c11272a6b02ea211da307137349df80f2d3b0c3e3f569e66d7f3bde53ba14")]
public sealed record ReadyEventPayload(long Intents, Guid[] SpaceIds);

[BotEventDefinition(BotEventType.Heartbeat, "Connection")]
[BotEventDescription("Keep-alive ping sent every 30 seconds. Contains cursor for reconnection.")]
[StableEventContract("9ff546a10894b8731d8418aa3d9868123b6111debae5969495377e3da81362c9")]
public sealed record HeartbeatEventPayload(long Timestamp);

[BotEventDefinition(BotEventType.Resumed, "Connection")]
[BotEventDescription("Sent after replaying missed events on reconnection via Last-Event-ID.")]
[StableEventContract("4b687afe115130e27be5dc122c3a46d2c0133e5a946a340264030928b8069d30")]
public sealed record ResumedEventPayload(int ReplayedCount);

// ─── Messages ────────────────────────────────────────────

[BotEventDefinition(BotEventType.MessageCreate, "Messages", Intent = BotIntent.Messages)]
[BotEventDescription("A new message was sent in a channel the bot has access to.")]
public sealed record MessageCreateEvent(
    Guid         SpaceId,
    Guid         ChannelId,
    BotMessageV1 Message);

[BotEventDefinition(BotEventType.MessageEdit, "Messages", Intent = BotIntent.Messages)]
[BotEventDescription("A message was edited in a channel the bot has access to.")]
public sealed record MessageEditEvent(
    Guid           SpaceId,
    Guid           ChannelId,
    long           MessageId,
    string?        Text,
    DateTimeOffset UpdatedAt);

// ─── Members ─────────────────────────────────────────────

[BotEventDefinition(BotEventType.MemberJoin, "Members", Intent = BotIntent.Members)]
[BotEventDescription("A user joined a space the bot is in.")]
public sealed record MemberJoinEvent(
    Guid      SpaceId,
    BotUserV1 User);

[BotEventDefinition(BotEventType.MemberLeave, "Members", Intent = BotIntent.Members)]
[BotEventDescription("A user left or was removed from a space the bot is in.")]
public sealed record MemberLeaveEvent(
    Guid SpaceId,
    Guid UserId);

[BotEventDefinition(BotEventType.MemberUpdate, "Members", Intent = BotIntent.Members)]
[BotEventDescription("A member's profile or roles were updated in a space.")]
public sealed record MemberUpdateEvent(
    Guid      SpaceId,
    BotUserV1 User);

// ─── Channels ────────────────────────────────────────────

[BotEventDefinition(BotEventType.ChannelCreate, "Channels", Intent = BotIntent.Channels)]
[BotEventDescription("A new channel was created in a space.")]
public sealed record ChannelCreateEvent(
    Guid             SpaceId,
    BotChannelFullV1 Channel);

[BotEventDefinition(BotEventType.ChannelDelete, "Channels", Intent = BotIntent.Channels)]
[BotEventDescription("A channel was deleted from a space.")]
public sealed record ChannelDeleteEvent(
    Guid SpaceId,
    Guid ChannelId);

// ─── Presence ────────────────────────────────────────────

[BotEventDefinition(BotEventType.PresenceUpdate, "Presence", Intent = BotIntent.Presence)]
[BotEventDescription("A user's online status or activity changed.")]
public sealed record PresenceUpdateEvent(
    Guid          SpaceId,
    BotUserV1     User,
    BotPresenceV1 Presence);

// ─── Voice ───────────────────────────────────────────────

[BotEventDefinition(BotEventType.VoiceJoin, "Voice", Intent = BotIntent.Voice)]
[BotEventDescription("A user joined a voice channel.")]
public sealed record VoiceJoinEvent(
    Guid      SpaceId,
    Guid      ChannelId,
    BotUserV1 User);

[BotEventDefinition(BotEventType.VoiceLeave, "Voice", Intent = BotIntent.Voice)]
[BotEventDescription("A user left a voice channel.")]
public sealed record VoiceLeaveEvent(
    Guid      SpaceId,
    Guid      ChannelId,
    BotUserV1 User);

// ─── Calls ───────────────────────────────────────────────

[BotEventDefinition(BotEventType.CallIncoming, "Calls", Intent = BotIntent.Calls)]
[BotEventDescription("An incoming call is ringing for the bot.")]
public sealed record CallIncomingEvent(
    Guid CallId,
    Guid FromUserId);

[BotEventDefinition(BotEventType.CallEnded, "Calls", Intent = BotIntent.Calls)]
[BotEventDescription("A call the bot was involved in has ended.")]
public sealed record CallEndedEvent(
    Guid CallId);

// ─── Bot Lifecycle ───────────────────────────────────────

[BotEventDefinition(BotEventType.BotInstallingToSpace, "BotLifecycle")]
[BotEventDescription("Sent directly to the bot when a server administrator installs it into a space. Always delivered regardless of intents.")]
public sealed record BotInstallingToSpaceEvent(
    Guid SpaceId);

[BotEventDefinition(BotEventType.BotUninstallingFromSpace, "BotLifecycle")]
[BotEventDescription("Sent directly to the bot when a server administrator uninstalls it from a space. Always delivered regardless of intents.")]
public sealed record BotUninstallingFromSpaceEvent(
    Guid SpaceId);

// ─── Commands ────────────────────────────────────────────

[BotEventDefinition(BotEventType.CommandInteraction, "Commands", Intent = BotIntent.Commands)]
[BotEventDescription("A user invoked a slash command registered by the bot.")]
public sealed record CommandInteractionEvent(
    Guid                          InteractionId,
    Guid                          SpaceId,
    Guid                          ChannelId,
    Guid                          CommandId,
    string                        CommandName,
    BotUserV1                     User,
    List<BotCommandOptionValueV1> Options);

// ─── Typing ──────────────────────────────────────────────

[BotEventDefinition(BotEventType.TypingStart, "Typing", Intent = BotIntent.Typing)]
[BotEventDescription("A user or bot started typing in a channel.")]
public sealed record TypingStartEvent(
    Guid      SpaceId,
    Guid      ChannelId,
    Guid      UserId,
    string?   Kind);

[BotEventDefinition(BotEventType.TypingStop, "Typing", Intent = BotIntent.Typing)]
[BotEventDescription("A user or bot stopped typing in a channel.")]
public sealed record TypingStopEvent(
    Guid SpaceId,
    Guid ChannelId,
    Guid UserId);

// ─── Archetypes ──────────────────────────────────────────

[BotEventDefinition(BotEventType.ArchetypeCreate, "Archetypes", Intent = BotIntent.Archetypes)]
[BotEventDescription("A new archetype (role) was created in a space.")]
public sealed record ArchetypeCreateEvent(
    Guid            SpaceId,
    BotArchetypeV1  Archetype);

[BotEventDefinition(BotEventType.ArchetypeUpdate, "Archetypes", Intent = BotIntent.Archetypes)]
[BotEventDescription("An archetype (role) was updated in a space.")]
public sealed record ArchetypeUpdateEvent(
    Guid            SpaceId,
    BotArchetypeV1  Archetype);

// ─── Control Interactions ────────────────────────────────

[BotEventDefinition(BotEventType.ControlInteraction, "ControlInteractions", Intent = BotIntent.ControlInteractions)]
[BotEventDescription("A user clicked an interactive button on a message.")]
public sealed record ControlInteractionEvent(
    Guid        InteractionId,
    ControlType ControlType,
    long        MessageId,
    Guid        ChannelId,
    Guid        SpaceId,
    BotUserV1   User,
    string      ControlId);

[BotEventDefinition(BotEventType.SelectInteraction, "ControlInteractions", Intent = BotIntent.ControlInteractions)]
[BotEventDescription("A user submitted a selection from a select menu on a message.")]
public sealed record SelectInteractionEvent(
    Guid          InteractionId,
    ControlType   ControlType,
    string        CustomId,
    long          MessageId,
    Guid          ChannelId,
    Guid          SpaceId,
    BotUserV1     User,
    List<string>  Values);

// ─── Modal Submit ────────────────────────────────────────

[BotEventDefinition(BotEventType.ModalSubmit, "ControlInteractions", Intent = BotIntent.ControlInteractions)]
[BotEventDescription("A user submitted a modal popup form.")]
public sealed record ModalSubmitEvent(
    Guid                    InteractionId,
    string                  CustomId,
    Guid                    ChannelId,
    Guid                    SpaceId,
    BotUserV1               User,
    List<ModalSubmitValueV1> Values);
