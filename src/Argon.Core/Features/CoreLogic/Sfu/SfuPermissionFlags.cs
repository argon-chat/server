namespace Argon.Sfu;

using Livekit.Server.Sdk.Dotnet;

public enum SfuPermissionKind
{
    DefaultUser,
    DefaultAdmin,
    DefaultBot
}

/// <summary>What a participant may publish and whether it may listen, on top of its kind's grants.</summary>
[Flags]
public enum SfuMediaRights
{
    None        = 0,
    Microphone  = 1 << 0,
    Camera      = 1 << 1,
    ScreenShare = 1 << 2,
    Listen      = 1 << 3,
    All         = Microphone | Camera | ScreenShare | Listen
}

public record SfuPermission
{
    public static VideoGrants For(SfuPermissionKind flag, string roomId, SfuMediaRights rights)
    {
        var grants = For(flag, roomId);
        if (rights == SfuMediaRights.All)
            return grants;

        // LiveKit reads an empty source list as "any source", so no source at all has to be CanPublish = false.
        var sources = PublishSources(rights);
        grants.CanPublish        = sources.Count > 0;
        grants.CanPublishSources = sources.Select(x => x.ToFormatString()).ToList();
        grants.CanSubscribe      = rights.HasFlag(SfuMediaRights.Listen);
        return grants;
    }

    public static List<TrackSource> PublishSources(SfuMediaRights rights)
    {
        var sources = new List<TrackSource>();
        if (rights.HasFlag(SfuMediaRights.Microphone))
            sources.Add(TrackSource.Microphone);
        if (rights.HasFlag(SfuMediaRights.Camera))
            sources.Add(TrackSource.Camera);
        if (rights.HasFlag(SfuMediaRights.ScreenShare))
        {
            sources.Add(TrackSource.ScreenShare);
            sources.Add(TrackSource.ScreenShareAudio);
        }
        return sources;
    }

    public static VideoGrants For(SfuPermissionKind flag, string roomId)
        => flag switch
        {
            SfuPermissionKind.DefaultUser  => DefaultUser(roomId),
            SfuPermissionKind.DefaultAdmin => DefaultAdmin(roomId),
            SfuPermissionKind.DefaultBot   => DefaultUser(roomId),
            _                              => throw new ArgumentOutOfRangeException(nameof(flag), flag, null)
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

    /// <summary>A radio participant: publishes a microphone into the radio room and hears nothing.</summary>
    public static VideoGrants Radio(string roomId) =>
        new()
        {
            RoomJoin             = true,
            Room                 = roomId,
            CanPublish           = true,
            CanPublishSources    = [TrackSource.Microphone.ToFormatString()],
            CanSubscribe         = false,
            CanPublishData       = false,
            CanUpdateOwnMetadata = false,
            Hidden               = false
        };

    public static VideoGrants DefaultAdmin(string roomId) =>
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
            Hidden               = true,
            IngressAdmin         = true,
            Recorder             = true,
            RoomAdmin            = true,
            RoomList             = true,
            RoomRecord           = true
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