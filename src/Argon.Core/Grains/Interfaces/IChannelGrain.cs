namespace Argon.Grains.Interfaces;

using Argon.Features.BotApi;
using Orleans.Concurrency;

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

    [Alias("UpdateChannel")]
    Task<ChannelEntity> UpdateChannel(ChannelInput input);

    /// <summary>
    /// Partial update behind the ion <c>UpdateChannel</c>: every argument is optional and null means
    /// "leave alone". <paramref name="slowModeSeconds"/> is the exception — 0 clears the cooldown —
    /// which is why it cannot be folded into <see cref="ChannelInput"/>, whose fields are all
    /// mandatory replacements.
    /// </summary>
    [Alias("UpdateChannelSettings")]
    Task<Either<ChannelEntity, UpdateChannelError>> UpdateChannelSettings(string? name, string? description, int? slowModeSeconds,
        CancellationToken ct = default);

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
    Task<long> SendMessage(string text, List<IMessageEntity> entities, long randomId, long? replyTo, List<ControlRowV1>? controls = null);

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


    [OneWay, Alias("OnTypingEmit")]
    ValueTask OnTypingEmit();
    [OneWay, Alias("OnTypingStopEmit")]
    ValueTask OnTypingStopEmit();

    [OneWay, Alias("OnBotTypingEmit")]
    ValueTask OnBotTypingEmit(TypingKind kind);


    [Alias("KickMemberFromChannel")]
    Task<bool> KickMemberFromChannel(Guid memberId);

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

    [Alias(nameof(AddReaction))]
    Task<IAddReactionResult> AddReaction(long messageId, string emoji);

    [Alias(nameof(RemoveReaction))]
    Task<IRemoveReactionResult> RemoveReaction(long messageId, string emoji);

    [Alias(nameof(BatchGetReactions))]
    Task<Dictionary<long, List<ReactionInfo>>> BatchGetReactions(List<long> messageIds);
}

/// <summary>Realtime state for a channel.</summary>
[GenerateSerializer, Immutable]
public sealed record ChannelRealtimeState(
    [property: Id(0)] List<RealtimeChannelUser> Members);


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