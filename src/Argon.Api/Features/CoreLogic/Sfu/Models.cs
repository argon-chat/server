namespace Argon.Sfu;

using Grpc.Core;
using LiveKit.Proto;
using Orleans.BroadcastChannel;


public record EphemeralChannelInfo(ISfuRoomDescriptor channelId, string sid, Room room);



public record ArgonUserId([field: Id(0)] Guid id)
{
    public string ToRawIdentity() => id.ToString();

    public static implicit operator ArgonUserId(Guid userId) => new(userId);
}


public record ArgonServerId([field: Id(0)] Guid id);

public record ArgonChannelId([field: Id(0)] ArgonServerId serverId, [field: Id(1)] Guid channelId) : ISfuRoomDescriptor
{
    public string ToRawRoomId() => $"{serverId.id}-{channelId}";
}


public record ArgonMeetId(string meetId) : ISfuRoomDescriptor
{
    public string ToRawRoomId() => $"{meetId}";
}


public interface ISfuRoomDescriptor
{
    string ToRawRoomId();
}