namespace Argon.Sfu;

using LiveKit.Proto;

public record struct EphemeralChannelInfo(ArgonChannelId channelId, string sid, Room room);

public record struct RealtimeToken(string value);

public record struct ArgonUserId(Guid id)
{
    public string ToRawIdentity()
    {
        return id.ToString("N");
    }
}

public record struct ArgonServerId(Guid id);

public record struct ArgonChannelId(ArgonServerId serverId, Guid channelId)
{
    public string ToRawRoomId()
    {
        return $"{serverId.id:N}:{channelId:N}";
    }
}