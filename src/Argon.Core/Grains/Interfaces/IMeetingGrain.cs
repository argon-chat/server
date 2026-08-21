namespace Argon.Grains.Interfaces;

/// <summary>
/// Grain for managing meeting lifecycle and state.
/// Key: meetId (Guid as string)
/// </summary>
[Alias("Argon.Grains.Interfaces.IMeetingGrain")]
public interface IMeetingGrain : IGrainWithStringKey
{
    /// <summary>
    /// Creates a new meeting with the specified host.
    /// </summary>
    [Alias("CreateAsync")]
    Task<MeetingCreatedResult> CreateAsync(
        Guid hostUserId,
        string hostDisplayName,
        string? hostAvatarFileId,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a new anonymous meeting without authentication.
    /// </summary>
    [Alias("CreateAnonymousAsync")]
    Task<AnonymousMeetingCreatedResult> CreateAnonymousAsync(
        AnonymousHostInfoDto hostInfo,
        MeetingLimitsDto limits,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the current meeting state.
    /// </summary>
    [Alias("GetStateAsync")]
    Task<MeetingState?> GetStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Attempts to join the meeting directly (when waiting room is disabled).
    /// </summary>
    [Alias("JoinAsync")]
    Task<MeetingJoinResult> JoinAsync(
        Guid? userId,
        string displayName,
        string? avatarFileId,
        CancellationToken ct = default);

    /// <summary>
    /// Updates meeting settings.
    /// </summary>
    [Alias("UpdateSettingsAsync")]
    Task<bool> UpdateSettingsAsync(
        Guid requesterId,
        MeetingSettingsDto settings,
        CancellationToken ct = default);

    /// <summary>
    /// Ends the meeting and disconnects all participants.
    /// </summary>
    [Alias("EndMeetingAsync")]
    Task<bool> EndMeetingAsync(Guid requesterId, CancellationToken ct = default);

    /// <summary>
    /// Kicks a participant from the meeting.
    /// </summary>
    [Alias("KickParticipantAsync")]
    Task<bool> KickParticipantAsync(
        Guid requesterId,
        string participantIdentity,
        string? reason,
        CancellationToken ct = default);

    /// <summary>
    /// Mutes a participant.
    /// </summary>
    [Alias("MuteParticipantAsync")]
    Task<bool> MuteParticipantAsync(
        Guid requesterId,
        string participantIdentity,
        CancellationToken ct = default);

    /// <summary>
    /// Transfers host role to another participant.
    /// </summary>
    [Alias("TransferHostAsync")]
    Task<bool> TransferHostAsync(
        Guid requesterId,
        string newHostIdentity,
        CancellationToken ct = default);

    /// <summary>
    /// Admits a participant from the waiting room.
    /// </summary>
    [Alias("AdmitParticipantAsync")]
    Task<ParticipantAdmitResult> AdmitParticipantAsync(
        Guid requesterId,
        Guid requestId,
        CancellationToken ct = default);

    /// <summary>
    /// Denies a participant's join request.
    /// </summary>
    [Alias("DenyParticipantAsync")]
    Task<bool> DenyParticipantAsync(
        Guid requesterId,
        Guid requestId,
        string? reason,
        CancellationToken ct = default);

    /// <summary>
    /// Admits all waiting participants.
    /// </summary>
    [Alias("AdmitAllAsync")]
    Task<int> AdmitAllAsync(Guid requesterId, CancellationToken ct = default);

    /// <summary>
    /// Starts recording the meeting.
    /// </summary>
    [Alias("StartRecordingAsync")]
    Task<bool> StartRecordingAsync(
        Guid requesterId,
        string recorderIdentity,
        string recorderName,
        CancellationToken ct = default);

    /// <summary>
    /// Stops recording the meeting.
    /// </summary>
    [Alias("StopRecordingAsync")]
    Task<bool> StopRecordingAsync(Guid requesterId, CancellationToken ct = default);

    /// <summary>
    /// Called when a participant leaves the room.
    /// </summary>
    [Alias("OnParticipantLeftAsync")]
    Task OnParticipantLeftAsync(string participantIdentity, CancellationToken ct = default);

    /// <summary>
    /// Cancels a join request from the waiting room.
    /// </summary>
    [Alias("CancelJoinRequestAsync")]
    Task<bool> CancelJoinRequestAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// Gets the waiting room list.
    /// </summary>
    [Alias("GetWaitingListAsync")]
    Task<List<WaitingParticipantDto>> GetWaitingListAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the status of a join request for polling.
    /// </summary>
    [Alias("GetJoinRequestStatusAsync")]
    Task<JoinRequestStatusResult> GetJoinRequestStatusAsync(Guid requestId, CancellationToken ct = default);

    /// <summary>
    /// Creates a meeting linked to a space channel (chat disabled by default).
    /// </summary>
    [Alias("CreateLinkedAsync")]
    Task<MeetingCreatedResult> CreateLinkedAsync(
        Guid spaceId,
        Guid channelId,
        Guid hostUserId,
        string hostDisplayName,
        string? hostAvatarFileId,
        CancellationToken ct = default);
}

[GenerateSerializer, Immutable]
public sealed record MeetingCreatedResult(
    [property: Id(0)] Guid MeetId,
    [property: Id(1)] string InviteCode,
    [property: Id(2)] string Token,
    [property: Id(3)] string RtcEndpoint,
    [property: Id(4)] IceEndpointDto[] IceEndpoints);

[GenerateSerializer, Immutable]
public sealed record MeetingJoinResult(
    [property: Id(0)] bool Success,
    [property: Id(1)] string? Token,
    [property: Id(2)] bool IsHost,
    [property: Id(3)] MeetingJoinError Error,
    [property: Id(4)] bool RequiresApproval,
    [property: Id(5)] Guid? RequestId);

[GenerateSerializer, Immutable]
public sealed record ParticipantAdmitResult(
    [property: Id(0)] bool Success,
    [property: Id(1)] string? ParticipantIdentity,
    [property: Id(2)] string? Error);

[GenerateSerializer, Immutable]
public sealed record JoinRequestStatusResult(
    [property: Id(0)] JoinRequestStatus Status,
    [property: Id(1)] string? DenialReason,
    [property: Id(2)] Guid? MeetId,
    [property: Id(3)] string? Token,
    [property: Id(4)] string? RtcEndpoint,
    [property: Id(5)] IceEndpointDto[] IceEndpoints,
    [property: Id(6)] int Position);

/// <summary>
/// Result of creating an anonymous meeting.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record AnonymousMeetingCreatedResult(
    [property: Id(0)] Guid MeetId,
    [property: Id(1)] string InviteCode,
    [property: Id(2)] string Token,
    [property: Id(3)] Guid HostSessionId,
    [property: Id(4)] string RtcEndpoint,
    [property: Id(5)] IceEndpointDto[] IceEndpoints,
    [property: Id(6)] MeetingLimitsDto AppliedLimits,
    [property: Id(7)] DateTimeOffset ExpiresAt);

/// <summary>
/// Anonymous host information DTO for Orleans serialization.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record AnonymousHostInfoDto(
    [property: Id(0)] Guid SessionId,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] string IpAddress,
    [property: Id(3)] string? ClientFingerprint,
    [property: Id(4)] string? Region,
    [property: Id(5)] DateTimeOffset CreatedAt);

/// <summary>
/// Meeting limits DTO for Orleans serialization.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record MeetingLimitsDto(
    [property: Id(0)] int? MaxDurationMinutes,
    [property: Id(1)] int MaxParticipants,
    [property: Id(2)] bool AllowScreenShare,
    [property: Id(3)] bool AllowRecording,
    [property: Id(4)] bool AllowChat,
    [property: Id(5)] bool AllowWaitingRoom,
    [property: Id(6)] bool AllowRoomSettings,
    [property: Id(7)] UserTierDto OwnerTier);

/// <summary>
/// User tier enumeration for meeting limits.
/// </summary>
[GenerateSerializer]
public enum UserTierDto
{
    Anonymous = 0,
    Registered = 1,
}

public enum MeetingJoinError
{
    None = 0,
    InvalidCode = 1,
    MeetingEnded = 2,
    MeetingFull = 3,
    BannedFromMeeting = 4,
    ServiceUnavailable = 5
}

public enum JoinRequestStatus
{
    Waiting = 0,
    Approved = 1,
    Denied = 2,
    NotFound = 3,
    Expired = 4
}

[GenerateSerializer, Immutable]
public sealed record MeetingState(
    [property: Id(0)] Guid MeetId,
    [property: Id(1)] string InviteCode,
    [property: Id(2)] string HostIdentity,
    [property: Id(3)] Guid? HostUserId,
    [property: Id(4)] string HostDisplayName,
    [property: Id(5)] MeetingSettingsDto Settings,
    [property: Id(6)] int ParticipantCount,
    [property: Id(7)] DateTimeOffset CreatedAt,
    [property: Id(8)] bool IsRecording,
    [property: Id(9)] string? RecorderIdentity,
    [property: Id(10)] bool IsEnded,
    [property: Id(11)] Guid? AnonymousHostSessionId = null);

[GenerateSerializer, Immutable]
public sealed record MeetingSettingsDto(
    [property: Id(0)] bool AllowAnyone,
    [property: Id(1)] bool AllowChat,
    [property: Id(2)] bool AllowScreenShare,
    [property: Id(3)] bool AllowVideo,
    [property: Id(4)] int MaxParticipants)
{
    public static MeetingSettingsDto Default => new(
        AllowAnyone: false,
        AllowChat: true,
        AllowScreenShare: true,
        AllowVideo: true,
        MaxParticipants: 100);
}

[GenerateSerializer, Immutable]
public sealed record IceEndpointDto(
    [property: Id(0)] string Endpoint,
    [property: Id(1)] string Username,
    [property: Id(2)] string Password);

[GenerateSerializer, Immutable]
public sealed record WaitingParticipantDto(
    [property: Id(0)] Guid RequestId,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] Guid? UserId,
    [property: Id(3)] string? AvatarFileId,
    [property: Id(4)] DateTimeOffset RequestedAt);
