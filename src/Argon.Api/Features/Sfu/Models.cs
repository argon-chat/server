namespace Argon.Sfu;

using LiveKit.Proto;
[MessagePackObject(true)]
public record EphemeralChannelInfo(ArgonChannelId channelId, string sid, Room room);


[MessagePackObject(true)]
public record ArgonUserId([field: Id(0)] Guid id)
{
    public string ToRawIdentity() => id.ToString("N");

    public static implicit operator ArgonUserId(Guid userId) => new(userId);
}

[MessagePackObject(true)]
public record ArgonServerId([field: Id(0)] Guid id);
[MessagePackObject(true)]
public record ArgonChannelId([field: Id(0)] ArgonServerId serverId, [field: Id(1)] Guid channelId)
{
    public string ToRawRoomId() => $"{serverId.id:N}:{channelId:N}";
}