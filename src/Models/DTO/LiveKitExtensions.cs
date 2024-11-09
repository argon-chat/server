namespace Models.DTO;

using LiveKit.Proto;
using Microsoft.OpenApi.Extensions;

public static class LiveKitExtensions
{
    public static string ToFormatString(this TrackSource trackSource) => trackSource switch
    {
        TrackSource.Camera           => "camera",
        TrackSource.Microphone       => "microphone",
        TrackSource.ScreenShare      => "screen_share",
        TrackSource.ScreenShareAudio => "screen_share_audio",
        _                            => throw new ArgumentOutOfRangeException(nameof(trackSource), trackSource, null)
    };

    public static IEnumerable<T> EnumerateFlags<T>(this T @enum) where T : Enum =>
        from Enum value in Enum.GetValues(@enum.GetType()) where @enum.HasFlag(value) select (T)value;

    public static List<string> ToList(this SfuPermissionFlags sfuPermissionFlags) => sfuPermissionFlags.EnumerateFlags()
       .Select(permission => permission.GetAttributeOfType<FlagNameAttribute>()).Where(x => x is not null).Select(attr => attr.FlagName).ToList();
}