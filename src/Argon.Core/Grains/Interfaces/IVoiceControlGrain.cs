namespace Argon.Core.Grains.Interfaces;

using Sfu;

[Alias("IVoiceControlGrain")]
public interface IVoiceControlGrain : IGrainWithGuidKey
{
    Task<string> IssueAuthorizationTokenAsync(ArgonUserId userId, ArgonRoomId roomId, SfuPermission permission, CancellationToken ct = default);

    Task<bool> SetMuteParticipantAsync(bool isMuted, string sid, ArgonUserId userId, ArgonRoomId channelId, CancellationToken ct = default);

    Task<bool> KickParticipantAsync(ArgonUserId userId, ArgonRoomId channelId, CancellationToken ct = default);

    Task<string> BeginRecordAsync(ArgonRoomId channelId, CancellationToken ct = default);

    Task<RtcEndpoint> GetRtcEndpointAsync(CancellationToken ct = default);

    Task<bool> StopRecordAsync(ArgonRoomId channelId, string egressId, CancellationToken ct = default);

    Task<string> InterlinkCallToPhone(ArgonRoomId roomId, ArgonUserId from, string phoneNumberTo, CancellationToken ct = default);
}