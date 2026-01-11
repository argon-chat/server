namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

[Alias("Argon.Grains.Interfaces.IChannelGrain")]
public interface IChannelGrain : IGrainWithGuidKey
{
    [Alias("Join")]
    Task<Either<string, JoinToChannelError>> Join();

    [Alias("Leave")]
    Task Leave(Guid userId);

    [Alias("UpdateChannel")]
    Task<ChannelEntity> UpdateChannel(ChannelInput input);

    [Alias(nameof(SendMessage))]
    Task<long> SendMessage(string text, List<IMessageEntity> entities, long randomId, long? replyTo);

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


    [Alias("KickMemberFromChannel")]
    Task<bool> KickMemberFromChannel(Guid memberId);

    [Alias("BeginRecord")]
    Task<bool> BeginRecord(CancellationToken ct = default);
    [Alias("StopRecord")]
    Task<bool> StopRecord(CancellationToken ct = default);

    /// <summary>
    /// Creates a linked meeting for this voice channel.
    /// </summary>
    [Alias("CreateLinkedMeetingAsync")]
    Task<ChannelMeetingResult?> CreateLinkedMeetingAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the invite link for the linked meeting, if exists.
    /// </summary>
    [Alias("GetMeetingLinkAsync")]
    Task<string?> GetMeetingLinkAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets full linked meeting info, if exists.
    /// </summary>
    [Alias("GetLinkedMeetingInfoAsync")]
    Task<LinkedMeetingInfo?> GetLinkedMeetingInfoAsync(CancellationToken ct = default);

    /// <summary>
    /// Ends the linked meeting.
    /// </summary>
    [Alias("EndLinkedMeetingAsync")]
    Task<bool> EndLinkedMeetingAsync(CancellationToken ct = default);
}

/// <summary>
/// Combined realtime state for a channel - members and meeting info.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record ChannelRealtimeState(
    [property: Id(0)] List<RealtimeChannelUser> Members,
    [property: Id(1)] LinkedMeetingInfo? MeetingInfo);


public sealed record ChannelInput(
    string Name,
    string? Description,
    ChannelType ChannelType);

/// <summary>
/// Result of creating a linked meeting from a channel.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record ChannelMeetingResult(
    [property: Id(0)] Guid MeetId,
    [property: Id(1)] string InviteCode,
    [property: Id(2)] string InviteLink);

public sealed record ParticipantInfo(
    string UserId,
    string UserName,
    bool IsMicEnabled,
    bool IsCameraEnabled);