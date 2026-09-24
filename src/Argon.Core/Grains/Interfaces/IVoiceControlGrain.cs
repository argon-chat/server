namespace Argon.Core.Grains.Interfaces;

using Livekit.Server.Sdk.Dotnet;
using Sfu;

[Alias("IVoiceControlGrain")]
public interface IVoiceControlGrain : IGrainWithGuidKey
{
    Task<string> IssueAuthorizationTokenAsync(ArgonUserId userId, ArgonRoomId roomId, SfuPermissionKind permission,
        SfuMediaRights rights = SfuMediaRights.All, CancellationToken ct = default);

    /// <summary>
    /// Replaces what a connected participant may publish and hear. Revoking a source makes LiveKit
    /// unpublish that track; false when the participant is not in the room or LiveKit refused.
    /// </summary>
    Task<bool> UpdateParticipantRightsAsync(ArgonUserId userId, ArgonRoomId roomId, SfuMediaRights rights, CancellationToken ct = default);

    Task<bool> KickParticipantAsync(ArgonUserId userId, ArgonRoomId channelId, CancellationToken ct = default);

    Task<string> BeginRecordAsync(ArgonRoomId channelId, CancellationToken ct = default);

    Task<RtcEndpoint> GetRtcEndpointAsync(CancellationToken ct = default);

    Task<bool> StopRecordAsync(ArgonRoomId channelId, string egressId, CancellationToken ct = default);
}