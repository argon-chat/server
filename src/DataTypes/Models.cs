namespace DataTypes;

using LiveKit.Proto;
using MemoryPack;
using Orleans;

public record struct EphemeralChannelInfo(ArgonChannelId channelId, string sid, Room room);

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(RealtimeToken))]
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