namespace Argon.Grains.Interfaces;

using Argon.Features.BotApi;
using ion.runtime;
using Orleans.Concurrency;
using Sfu;

[Alias("Argon.Grains.Interfaces.IChannelGrain")]
public interface IChannelGrain : IGrainWithGuidKey
{
    [Alias("Join")]
    Task<Either<string, JoinToChannelError>> Join();

    [Alias("Leave")]
    Task Leave(Guid userId);

    /// <summary>
    /// Called by LiveKit webhook when a participant actually connects to the room.
    /// Registers the user in voice channel state and fires the join event.
    /// </summary>
    [Alias("OnParticipantJoined")]
    Task OnParticipantJoined(Guid userId);

    /// <summary>
    /// Partial update behind the ion <c>UpdateChannel</c>: every argument is optional and null means
    /// "leave alone". <paramref name="slowModeSeconds"/> and <paramref name="bitrate"/> are the
    /// exceptions — 0 clears them — which is why they cannot be folded into <see cref="ChannelInput"/>,
    /// whose fields are all mandatory replacements.
    /// </summary>
    [Alias("UpdateChannelSettings")]
    Task<Either<ChannelEntity, UpdateChannelError>> UpdateChannelSettings(string? name, string? description, int? slowModeSeconds,
        int? bitrate, CancellationToken ct = default);

    [Alias(nameof(SetChannelType))]
    Task<Either<ChannelEntity, UpdateChannelError>> SetChannelType(ChannelType type, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a message. Authors may always retract their own; anyone else needs
    /// <see cref="ArgonEntitlement.ManageMessages"/>.
    /// </summary>
    [Alias("DeleteMessage")]
    Task<DeleteMessageError> DeleteMessage(long messageId, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a message on a moderator's decision, with no caller to check. Reached only from
    /// the report grain, which has already established who decided and why; nothing on the wire
    /// maps to it. False when the message was already gone.
    /// </summary>
    [Alias("DeleteMessageByModeration")]
    Task<bool> DeleteMessageByModeration(long messageId, Guid operatorId, CancellationToken ct = default);

    /// <summary>
    /// Mints an invite code that points at this voice room. Returns the raw code; turning it into a
    /// link is the caller's job because the domain is configuration, not grain state.
    /// </summary>
    [Alias("CreateVoiceInvite")]
    Task<Either<string, VoiceInviteError>> CreateVoiceInvite(TimeSpan expiration, int maxUses, CancellationToken ct = default);

    [Alias(nameof(SendMessage))]
    Task<(SendMessageError error, long messageId)> SendMessage(string text, List<IMessageEntity> entities, long randomId, long? replyTo,
        List<ControlRowV1>? controls = null);

    [Alias(nameof(QueryMessages))]
    Task<List<ArgonMessageEntity>> QueryMessages(long? @from, int limit);

    [Alias("GetMembers")]
    Task<List<RealtimeChannelUser>> GetMembers();

    /// <summary>
    /// Gets realtime state (members + meeting info) in a single call for efficiency.
    /// </summary>
    [Alias("GetRealtimeStateAsync")]
    Task<ChannelRealtimeState> GetRealtimeStateAsync(CancellationToken ct = default);

    [OneWay, Alias("ClearChannel")]
    Task ClearChannel();

    /// <summary>
    /// The channel is being deleted: every member is removed from the room and the roster. One-way
    /// like <see cref="ClearChannel"/>, because <see cref="Leave"/> calls back into the space grain.
    /// </summary>
    [OneWay, Alias(nameof(TeardownVoiceAsync))]
    Task TeardownVoiceAsync();


    [OneWay, Alias("OnTypingEmit")]
    ValueTask OnTypingEmit();
    [OneWay, Alias("OnTypingStopEmit")]
    ValueTask OnTypingStopEmit();

    [OneWay, Alias("OnBotTypingEmit")]
    ValueTask OnBotTypingEmit(TypingKind kind);


    [Alias("KickMemberFromChannel")]
    Task<bool> KickMemberFromChannel(Guid memberId);

    /// <summary>
    /// Moves a member of this voice channel into <paramref name="targetChannelId"/>: the target admits
    /// them (<see cref="AdmitMovedMemberAsync"/>), this roster lets go, and the SFU moves the participant.
    /// </summary>
    [Alias(nameof(MoveVoiceMember))]
    Task<IMoveVoiceMemberResult> MoveVoiceMember(Guid memberId, Guid targetChannelId);

    /// <summary>
    /// The target's half of <see cref="MoveVoiceMember"/>: the member goes on this roster and the space's
    /// voice slot before the SFU moves them, and the rights this channel grants come back for the
    /// <c>UpdateParticipant</c> that follows the move. Not one-way: the move must not start before this is done.
    /// </summary>
    [Alias(nameof(AdmitMovedMemberAsync))]
    Task<VoiceAdmission> AdmitMovedMemberAsync(Guid userId);

    /// <summary>Broadcast ("radio") mode on with the default settings, or off. Needs ManageChannels on a voice channel.</summary>
    [Alias(nameof(SetBroadcastMode))]
    Task<ISetBroadcastSettingsResult> SetBroadcastMode(bool enabled);

    /// <summary>
    /// Sparse update of the broadcast settings: a field the patch leaves out stays, a cleared one goes
    /// back to its default. Needs ManageChannels and the mode on.
    /// </summary>
    [Alias(nameof(PatchBroadcastSettings))]
    Task<ISetBroadcastSettingsResult> PatchBroadcastSettings(IonPartial<BroadcastSettings> patch);

    /// <summary>
    /// A target channel was deleted: drop it from this channel's targets. This grain is the only
    /// writer of the broadcast column, so the space grain asks instead of editing the row — one-way,
    /// like <see cref="ReleaseMember"/>, because it asks during a delete and this can call back into
    /// the space grain.
    /// </summary>
    [OneWay, Alias(nameof(RemoveBroadcastTarget))]
    Task RemoveBroadcastTarget(Guid targetChannelId);

    /// <summary>The caller's own mute/deafen/stream flags; the server-set flags are kept as they are.</summary>
    [Alias(nameof(UpdateVoiceState))]
    Task UpdateVoiceState(ChannelMemberState state);

    /// <summary>Applies a space-wide server mute/deafen to a member currently in this channel.</summary>
    [OneWay, Alias(nameof(ApplyVoiceRestriction))]
    Task ApplyVoiceRestriction(Guid memberId, bool muted, bool deafened);

    /// <summary><see cref="Leave"/> without waiting: used when the member joined another voice channel.</summary>
    [OneWay, Alias(nameof(ReleaseMember))]
    Task ReleaseMember(Guid userId);

    /// <summary>
    /// Opens a screencast drawing session for the caller's active share. Validates the
    /// feature flag, computes the allowed-drawers set (CanDrawOnStream entitlement AND the
    /// streamer's "stream.draw" privacy rule) and broadcasts DrawingSessionStarted.
    /// </summary>
    [Alias("StartDrawingSession")]
    Task<Either<DrawingSessionDescriptor, DrawingDenyKind>> StartDrawingSession();

    /// <summary>Closes the drawing session (only the streamer who opened it may close it).</summary>
    [Alias("StopDrawingSession")]
    Task<bool> StopDrawingSession(string sessionId);

    [Alias("BeginRecord")]
    Task<bool> BeginRecord(CancellationToken ct = default);
    [Alias("StopRecord")]
    Task<bool> StopRecord(CancellationToken ct = default);

    [Alias(nameof(BeginUploadAttachment))]
    ValueTask<Either<UploadTicket, UploadFileError>> BeginUploadAttachment(CancellationToken ct = default);

    [Alias(nameof(CompleteUploadAttachment))]
    ValueTask<AttachmentInfo> CompleteUploadAttachment(Guid blobId, CancellationToken ct = default);

    [Alias(nameof(InvokeSlashCommand))]
    Task<IInvokeSlashCommandResult> InvokeSlashCommand(Guid commandId, List<SlashCommandOption> options);

    [Alias(nameof(InteractWithControl))]
    Task<IInteractWithControlResult> InteractWithControl(long messageId, string controlId);

    [Alias(nameof(InteractWithSelect))]
    Task<IInteractWithSelectResult> InteractWithSelect(long messageId, string customId, List<string> values);

    [Alias(nameof(SubmitModal))]
    Task<ISubmitModalResult> SubmitModal(Guid interactionId, List<ModalSubmitValue> values);

    [Alias(nameof(EditBotMessage))]
    Task EditBotMessage(long messageId, Guid botUserId, string? text, List<ControlRowV1>? controls);

    [Alias(nameof(EditMessage))]
    Task<IEditMessageResult> EditMessage(long messageId, string text, List<IMessageEntity> entities);

    [Alias(nameof(AddReaction))]
    Task<IAddReactionResult> AddReaction(long messageId, string emoji);

    [Alias(nameof(RemoveReaction))]
    Task<IRemoveReactionResult> RemoveReaction(long messageId, string emoji);

    [Alias(nameof(BatchGetReactions))]
    Task<Dictionary<long, List<ReactionInfo>>> BatchGetReactions(List<long> messageIds);

    /// <summary>Pins a message of this text or announcement channel. Needs ManageMessages; idempotent.</summary>
    [Alias(nameof(PinMessage))]
    Task<IPinMessageResult> PinMessage(long messageId, CancellationToken ct = default);

    [Alias(nameof(UnpinMessage))]
    Task<IUnpinMessageResult> UnpinMessage(long messageId, CancellationToken ct = default);

    /// <summary>The channel's pins, newest first; empty for a caller who cannot read its history.</summary>
    [Alias(nameof(GetPinnedMessages))]
    Task<List<PinnedMessage>> GetPinnedMessages(CancellationToken ct = default);

    /// <summary>Reactions on/off, post as space and show author of an announcement channel. Needs ManageChannels.</summary>
    [Alias(nameof(SetAnnouncementSettings))]
    Task<Either<ChannelEntity, UpdateChannelError>> SetAnnouncementSettings(bool reactions, bool postAsSpace, bool showAuthor,
        CancellationToken ct = default);

    /// <summary>
    /// Marks an announcement published and returns the copy with its targets. The caller delivers it
    /// (<see cref="ReceiveCrosspostAsync"/> per target), so this channel is not held during the fan-out.
    /// </summary>
    [Alias(nameof(PublishMessage))]
    Task<Either<CrosspostBatch, PublishMessageError>> PublishMessage(long messageId);

    /// <summary>Inserts a crosspost copy into this channel. Null when the channel does not take one.</summary>
    [Alias(nameof(ReceiveCrosspostAsync))]
    Task<long?> ReceiveCrosspostAsync(CrosspostDraft draft);
}

/// <summary>Realtime state for a channel.</summary>
[GenerateSerializer, Immutable]
public sealed record ChannelRealtimeState(
    [property: Id(0)] List<RealtimeChannelUser> Members);

/// <summary>What a moved member may publish and hear in the channel that admitted them.</summary>
[GenerateSerializer, Immutable]
public sealed record VoiceAdmission(
    [property: Id(0)] SfuMediaRights Rights);


public sealed record ChannelInput(
    string Name,
    string? Description,
    ChannelType ChannelType);

public sealed record ParticipantInfo(
    string UserId,
    string UserName,
    bool IsMicEnabled,
    bool IsCameraEnabled);

/// <summary>Grain-layer result of opening a screencast drawing session.</summary>
[GenerateSerializer, Immutable]
public sealed record DrawingSessionDescriptor(
    [property: Id(0)] string SessionId,
    [property: Id(1)] Guid StreamerId,
    [property: Id(2)] List<Guid> AllowedDrawers,
    [property: Id(3)] int DefaultTtlMs);

/// <summary>Why a drawing session could not be opened (mapped to the ion DrawingDenyReason).</summary>
public enum DrawingDenyKind
{
    None = 0,
    FeatureDisabled,
    NotStreaming,
    NoPermission,
    InternalError,
}