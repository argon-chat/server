namespace Argon.Api.Features.Sfu;

using LiveKit.Proto;
using Models.DTO;

public record SfuPermission(SfuPermissionFlags flags, List<TrackSource> allowedSources)
{
    public static readonly SfuPermission DefaultUser = new(
        SfuPermissionFlags.CAN_LISTEN | SfuPermissionFlags.CAN_PUBLISH | SfuPermissionFlags.ROOM_JOIN,
        [TrackSource.Microphone, TrackSource.Microphone]);

    public static readonly SfuPermission DefaultSystem = new(SfuPermissionFlags.ALL, []);

    public Dictionary<string, object> ToDictionary(ArgonChannelId channelId)
    {
        var dict = new Dictionary<string, object>();
        foreach (var key in flags.ToList())
            dict.Add(key, true);
        dict.Add("canPublishSources", allowedSources.Select(x => x.ToFormatString()).ToList());
        dict.Add("room", $"{channelId.serverId.id:N}:{channelId.channelId:N}");
        return dict;
    }
}