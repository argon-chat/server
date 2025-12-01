namespace Argon.Sfu;

using Livekit.Server.Sdk.Dotnet;

public enum SfuPermissionKind
{
    DefaultUser,
    DefaultAdmin,
    DefaultBot
}

public record SfuPermission
{
    public static VideoGrants For(SfuPermissionKind flag, string roomId)
        => flag switch
        {
            SfuPermissionKind.DefaultUser  => DefaultUser(roomId),
            SfuPermissionKind.DefaultAdmin => DefaultUser(roomId),
            SfuPermissionKind.DefaultBot   => DefaultUser(roomId)
        };

    public static VideoGrants DefaultUser(string roomId) =>
        new()
        {
            CanPublish           = true,
            RoomJoin             = true,
            CanSubscribeMetrics  = true,
            CanUpdateOwnMetadata = true,
            CanSubscribe         = true,
            RoomCreate           = true,
            Room                 = roomId,
            DestinationRoom      = roomId,
            CanPublishData       = true,
        };
}

[AttributeUsage(AttributeTargets.Field)]
public class FlagNameAttribute(string flagName) : Attribute
{
    public string FlagName { get; } = flagName;
}

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
}