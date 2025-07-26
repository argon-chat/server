namespace Argon.Sfu;

using Grpc.Core;
using LiveKit.Proto;
using Orleans.BroadcastChannel;

[MessagePackObject(true)]
public record EphemeralChannelInfo(ISfuRoomDescriptor channelId, string sid, Room room);


[MessagePackObject(true)]
public record ArgonUserId([field: Id(0)] Guid id)
{
    public string ToRawIdentity() => id.ToString();

    public static implicit operator ArgonUserId(Guid userId) => new(userId);
}

[MessagePackObject(true)]
public record ArgonServerId([field: Id(0)] Guid id);
[MessagePackObject(true)]
public record ArgonChannelId([field: Id(0)] ArgonServerId serverId, [field: Id(1)] Guid channelId) : ISfuRoomDescriptor
{
    public string ToRawRoomId() => $"{serverId.id}-{channelId}";
}

[MessagePackObject(true)]
public record ArgonMeetId(string meetId) : ISfuRoomDescriptor
{
    public string ToRawRoomId() => $"{meetId}";
}


public interface ISfuRoomDescriptor
{
    string ToRawRoomId();
}