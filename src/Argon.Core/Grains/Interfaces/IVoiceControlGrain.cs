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

    /// <summary>The broadcaster's radio token: <c>bc:{userId}</c> in <c>radio/{space}/{hq}</c>, publish-only, 15 minutes.</summary>
    Task<string> IssueRadioTokenAsync(Guid userId, Guid spaceId, Guid hqChannelId);

    /// <summary>Fork only (<c>Capabilities: Forward</c>): the participant appears in the destination room as a forwarded copy.</summary>
    Task<bool> ForwardParticipantAsync(string sourceRoom, string identity, string destinationRoom);

    /// <summary>
    /// Fork only (<c>Capabilities: Move</c>): the connected participant is moved from <paramref name="from"/> into
    /// <paramref name="to"/> on the same connection, keeping its tracks and — until the caller replaces them with
    /// <see cref="UpdateParticipantRightsAsync"/> — its permissions. False when LiveKit refused or the participant is not there.
    /// </summary>
    Task<bool> MoveParticipantAsync(ArgonUserId userId, ArgonRoomId from, ArgonRoomId to, CancellationToken ct = default);

    /// <summary><see cref="KickParticipantAsync"/> for raw names: radio rooms and <c>bc:</c> identities are not typed ids.</summary>
    Task<bool> RemoveParticipantAsync(string room, string identity);

    /// <summary>Empty on error, so a sweeper reading it removes nothing by mistake.</summary>
    Task<IReadOnlyList<SfuParticipant>> ListParticipantsAsync(string room);

    Task<bool> MutePublishedTrackAsync(string room, string identity, string trackSid, bool muted);
}

/// <summary>A participant as LiveKit lists it; <see cref="Forwarded"/> marks the fork's forwarded copy in a target room.</summary>
[GenerateSerializer, Immutable]
public sealed record SfuParticipant(
    [property: Id(0)] string Identity,
    [property: Id(1)] string Sid,
    [property: Id(2)] bool Forwarded,
    [property: Id(3)] IReadOnlyList<SfuTrack> Tracks);

[GenerateSerializer, Immutable]
public sealed record SfuTrack(
    [property: Id(0)] string Sid,
    [property: Id(1)] string Source,
    [property: Id(2)] bool Muted);
