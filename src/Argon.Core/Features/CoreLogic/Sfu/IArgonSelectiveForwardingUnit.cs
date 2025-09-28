namespace Argon.Sfu;

public interface IArgonSelectiveForwardingUnit
{
    const string CHANNEL_KEY = $":{nameof(IArgonSelectiveForwardingUnit)}";
    
    ValueTask<string> IssueAuthorizationTokenAsync(ArgonUserId userId, ISfuRoomDescriptor channelId, SfuPermission permission);
    ValueTask<string> IssueAuthorizationTokenForMeetAsync(string userName, ISfuRoomDescriptor channelId, SfuPermission permission);
    ValueTask<string> IssueAuthorizationTokenForMeetAsync(string userName, Guid sharedId, SfuPermission permission);

    ValueTask<bool> SetMuteParticipantAsync(bool isMuted, ArgonUserId userId, ISfuRoomDescriptor channelId);

    ValueTask<bool> KickParticipantAsync(ArgonUserId userId, ISfuRoomDescriptor channelId);

    ValueTask<EphemeralChannelInfo> EnsureEphemeralChannelAsync(ISfuRoomDescriptor channelId, uint maxParticipants);

    ValueTask<bool> PruneEphemeralChannelAsync(ISfuRoomDescriptor channelId);

    ValueTask<string> BeginRecordAsync(ISfuRoomDescriptor channelId);
    ValueTask<RtcEndpoint> GetRtcEndpointAsync();
}