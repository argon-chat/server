namespace Argon.Sfu;

using LiveKit.Proto;
using Microsoft.OpenApi.Extensions;

[Flags]
public enum SfuPermissionFlags
{
    NONE = 0,
    [FlagName(flagName: "roomCreate")]
    ROOM_CREATE = 1 << 1,
    [FlagName(flagName: "roomJoin")]
    ROOM_JOIN = 1 << 2,
    [FlagName(flagName: "canUpdateOwnMetadata")]
    UPDATE_METADATA = 1 << 3,
    [FlagName(flagName: "roomList")]
    ROOM_LIST = 1 << 4,
    [FlagName(flagName: "roomRecord")]
    ROOM_RECORD = 1 << 5,
    [FlagName(flagName: "roomAdmin")]
    ROOM_ADMIN = 1 << 6,
    [FlagName(flagName: "canPublish")]
    CAN_PUBLISH = 1 << 7,
    [FlagName(flagName: "canSubscribe")]
    CAN_LISTEN = 1 << 8,
    [FlagName(flagName: "hidden")]
    HIDDEN = 1 << 9,
    ALL = ROOM_CREATE | ROOM_JOIN | UPDATE_METADATA | ROOM_LIST | CAN_PUBLISH | ROOM_RECORD | ROOM_ADMIN | CAN_LISTEN
}

public record SfuPermission(SfuPermissionFlags flags, List<TrackSource> allowedSources)
{
    public static readonly SfuPermission DefaultUser = new(
        flags: SfuPermissionFlags.CAN_LISTEN | SfuPermissionFlags.CAN_PUBLISH |
               SfuPermissionFlags.ROOM_JOIN,
        allowedSources: [TrackSource.Microphone, TrackSource.Microphone]);

    public static readonly SfuPermission DefaultSystem = new(
        flags: SfuPermissionFlags.ALL, allowedSources: []);

    public Dictionary<string, object> ToDictionary(ArgonChannelId channelId)
    {
        var dict = new Dictionary<string, object>();
        foreach (var key in flags.ToList())
            dict.Add(key: key, value: true);
        dict.Add(key: "canPublishSources", value: allowedSources.Select(selector: x => x.ToFormatString()).ToList());
        dict.Add(key: "room", value: $"{channelId.serverId.id:N}:{channelId.channelId:N}");
        return dict;
    }
}

[AttributeUsage(validOn: AttributeTargets.Field)]
public class FlagNameAttribute(string flagName) : Attribute
{
    public string FlagName { get; } = flagName;
}

public static class LiveKitExtensions
{
    public static string ToFormatString(this TrackSource trackSource)
        => trackSource switch
           {
               TrackSource.Camera => "camera",
               TrackSource.Microphone => "microphone",
               TrackSource.ScreenShare => "screen_share",
               TrackSource.ScreenShareAudio => "screen_share_audio",
               _ => throw new ArgumentOutOfRangeException(paramName: nameof(trackSource), actualValue: trackSource, message: null)
           };

    public static IEnumerable<T> EnumerateFlags<T>(this T @enum) where T : Enum
        => from Enum value in Enum.GetValues(enumType: @enum.GetType()) where @enum.HasFlag(flag: value) select (T)value;

    public static List<string> ToList(this SfuPermissionFlags sfuPermissionFlags)
        => sfuPermissionFlags.EnumerateFlags()
                             .Select(selector: permission => permission.GetAttributeOfType<FlagNameAttribute>())
                             .Where(predicate: x => x is not null)
                             .Select(selector: attr => attr.FlagName).ToList();
}