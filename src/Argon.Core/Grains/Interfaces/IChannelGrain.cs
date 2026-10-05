namespace Argon.Grains.Interfaces;

using Argon.Api.Grains.Interfaces;
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

    /// <summary>
    /// <see cref="SendMessage"/> for the Bot API: attachments are the bot's uploads, made into entities from what
    /// was stored, and any the bot wrote itself are dropped.
    /// </summary>
    [Alias(nameof(SendBotMessage))]
    Task<BotMessageSent> SendBotMessage(BotMessageSend request);

    [Alias(nameof(QueryMessages))]
    Task<List<ArgonMessageEntity>> QueryMessages(long? @from, int limit);

    [Alias(nameof(QueryMessagesAround))]
    Task<MessageWindowEntity> QueryMessagesAround(long messageId, int older, int newer);

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

    [Alias("BeginRecord")]
    Task<bool> BeginRecord(CancellationToken ct = default);
    [Alias("StopRecord")]
    Task<bool> StopRecord(CancellationToken ct = default);

    [Alias(nameof(BeginUploadAttachment))]
    ValueTask<Either<UploadTicket, UploadFileError>> BeginUploadAttachment(CancellationToken ct = default);

    [Alias(nameof(CompleteUploadAttachment)), ResponseTimeout("00:01:00")]
    ValueTask<AttachmentInfo> CompleteUploadAttachment(Guid blobId, CancellationToken ct = default);

    /// <summary>
    /// A copy of an existing file as an attachment of this channel, without the bytes moving. Needs
    /// AttachFiles here and the right to read the source: its owner, or a member who can view the
    /// channel it was posted in.
    /// </summary>
    [Alias(nameof(AttachExistingFile))]
    ValueTask<Either<AttachmentInfo, AttachExistingFileError>> AttachExistingFile(Guid sourceFileId, string? fileName, CancellationToken ct = default);

    /// <summary>
    /// BeginUploadAttachment with the bytes described up front: a copy when the caller can already see
    /// a file with them, a ticket otherwise, an over-limit size refused before anything is signed.
    /// </summary>
    [Alias(nameof(PrepareUploadAttachment))]
    ValueTask<Either<PreparedUpload, PrepareUploadError>> PrepareUploadAttachment(byte[] sha256, long size, string contentType, string fileName,
        CancellationToken ct = default);

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

    /// <summary>
    /// Reacts with a live custom emoji; keyed as <c>:name:</c> plus the item id. An emoji of another space is taken only
    /// with <paramref name="allowForeign"/> and is refused with <c>INSUFFICIENT_PERMISSIONS</c> otherwise; an unknown or
    /// deleted one is refused with <c>NONE</c>.
    /// </summary>
    [Alias(nameof(AddCustomReaction))]
    Task<IAddReactionResult> AddCustomReaction(long messageId, Guid itemId, bool allowForeign = false);

    [Alias(nameof(RemoveCustomReaction))]
    Task<IRemoveReactionResult> RemoveCustomReaction(long messageId, Guid itemId);

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
    /// Marks an announcement published and hands it to its <see cref="ICrosspostDeliveryGrain"/>, which
    /// delivers it (<see cref="ReceiveCrosspostAsync"/> per target) after this returns.
    /// </summary>
    [Alias(nameof(PublishMessage))]
    Task<Either<PublishedCrosspost, PublishMessageError>> PublishMessage(long messageId);

    /// <summary>
    /// Inserts a crosspost copy into this channel, once per source message. Null when the channel does
    /// not take one; a repeat returns the copy already made.
    /// </summary>
    [Alias(nameof(ReceiveCrosspostAsync))]
    Task<long?> ReceiveCrosspostAsync(CrosspostDraft draft);
}

/// <remarks>Pass <see cref="List{T}"/>s: a collection expression's compiler-made type does not cross a grain call.</remarks>
[GenerateSerializer, Immutable]
public sealed record BotMessageSend(
    [property: Id(0)] string               Text,
    [property: Id(1)] List<IMessageEntity> Entities,
    [property: Id(2)] long                 RandomId,
    [property: Id(3)] long?                ReplyTo,
    [property: Id(4)] List<ControlRowV1>?  Controls,
    [property: Id(5)] List<Guid>           Attachments);

/// <summary>
/// <see cref="MessageId"/> and <see cref="Attachments"/> are set when <see cref="Error"/> is <c>NONE</c>.
/// <see cref="MissingUploads"/> lists the attachments that are no usable upload of the bot's, and comes with
/// <c>INVALID_DATA</c>.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record BotMessageSent(
    [property: Id(0)] SendMessageError              Error,
    [property: Id(1)] long                          MessageId,
    [property: Id(2)] List<MessageEntityAttachment> Attachments,
    [property: Id(3)] List<Guid>                    MissingUploads);

/// <summary>A stretch of a channel's history, newest first, and whether more lies on either side.</summary>
[GenerateSerializer, Immutable]
public sealed record MessageWindowEntity(
    [property: Id(0)] List<ArgonMessageEntity> Messages,
    [property: Id(1)] bool HasOlder,
    [property: Id(2)] bool HasNewer,
    [property: Id(3)] bool ContainsAnchor)
{
    public static readonly MessageWindowEntity Empty = new([], false, false, false);
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