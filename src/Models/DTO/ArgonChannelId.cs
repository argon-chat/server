namespace Models.DTO;

public record struct ArgonChannelId(ArgonServerId serverId, Guid channelId)
{
    public string ToRawRoomId() => $"{serverId.id:N}:{channelId:N}";
}