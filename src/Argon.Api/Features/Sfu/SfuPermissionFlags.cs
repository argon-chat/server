namespace Argon.Sfu;

using LiveKit.Proto;
using Microsoft.OpenApi.Extensions;

[Flags]
public enum SfuPermissionFlags
{
    NONE = 0,
    [FlagName("roomCreate")]
    ROOM_CREATE = 1 << 1,
    [FlagName("roomJoin")]
    ROOM_JOIN = 1 << 2,
    [FlagName("canUpdateOwnMetadata")]
    UPDATE_METADATA = 1 << 3,
    [FlagName("roomList")]
    ROOM_LIST = 1 << 4,
    [FlagName("roomRecord")]
    ROOM_RECORD = 1 << 5,
    [FlagName("roomAdmin")]
    ROOM_ADMIN = 1 << 6,
    [FlagName("canPublish")]
    CAN_PUBLISH = 1 << 7,
    [FlagName("canSubscribe")]
    CAN_LISTEN = 1 << 8,
    [FlagName("hidden")]
    HIDDEN = 1 << 9,
    ALL = ROOM_CREATE | ROOM_JOIN | UPDATE_METADATA | ROOM_LIST | CAN_PUBLISH | ROOM_RECORD | ROOM_ADMIN | CAN_LISTEN
}

public record SfuPermission(SfuPermissionFlags flags, List<TrackSource> allowedSources)
{
    public static readonly SfuPermission DefaultUser = new(
        SfuPermissionFlags.CAN_LISTEN | SfuPermissionFlags.CAN_PUBLISH | SfuPermissionFlags.ROOM_JOIN,
        [TrackSource.Microphone, TrackSource.Microphone]);

    public static readonly SfuPermission DefaultSystem = new(
        SfuPermissionFlags.ALL, []);

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

[AttributeUsage(AttributeTargets.Field)]
public class FlagNameAttribute(string flagName) : Attribute
{
    public string FlagName { get; } = flagName;
}

public static class LiveKitExtensions
{
    public static string ToFormatString(this TrackSource trackSource)
    {
        return trackSource switch
        {
            TrackSource.Camera => "camera",
            TrackSource.Microphone => "microphone",
            TrackSource.ScreenShare => "screen_share",
            TrackSource.ScreenShareAudio => "screen_share_audio",
            _ => throw new ArgumentOutOfRangeException(nameof(trackSource), trackSource, null)
        };
    }

    public static IEnumerable<T> EnumerateFlags<T>(this T @enum) where T : Enum
    {
        return from Enum value in Enum.GetValues(@enum.GetType()) where @enum.HasFlag(value) select (T)value;
    }

    public static List<string> ToList(this SfuPermissionFlags sfuPermissionFlags)
    {
        return sfuPermissionFlags.EnumerateFlags()
            .Select(permission => permission.GetAttributeOfType<FlagNameAttribute>())
            .Where(x => x is not null)
            .Select(attr => attr.FlagName).ToList();
    }
}