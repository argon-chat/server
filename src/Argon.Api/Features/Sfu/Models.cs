using LiveKit.Proto;

namespace Argon.Sfu;

public record struct EphemeralChannelInfo(ArgonChannelId channelId, string sid, Room room);

public record struct RealtimeToken(string value);

public record struct ArgonUserId(Guid id)
{
    public string ToRawIdentity() => id.ToString("N");
}

public record struct ArgonServerId(Guid id);

public record struct ArgonChannelId(ArgonServerId serverId, Guid channelId)
{
    public string ToRawRoomId() => $"{serverId.id:N}:{channelId:N}";
}