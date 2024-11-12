namespace Argon.Sfu;

using LiveKit.Proto;

public partial record struct EphemeralChannelInfo(ArgonChannelId channelId, string sid, Room room);

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(RealtimeToken))]
public partial record struct RealtimeToken([field: Id(0)] string value);

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(ArgonUserId))]
public partial record struct ArgonUserId([field: Id(0)] Guid id)
{
    public string ToRawIdentity() => id.ToString("N");

    public static implicit operator ArgonUserId(Guid userId) => new(userId);
}

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(ArgonServerId))]
public partial record struct ArgonServerId([field: Id(0)] Guid id);

[Serializable, GenerateSerializer, MemoryPackable, Alias(nameof(ArgonChannelId))]
public partial record struct ArgonChannelId([field: Id(0)] ArgonServerId serverId, [field: Id(1)] Guid channelId)
{
    public string ToRawRoomId() => $"{serverId.id:N}:{channelId:N}";
}