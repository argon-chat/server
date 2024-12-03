namespace Argon;

[TsInterface, MessagePackObject(true)]
public record ChannelJoinRequest(
    Guid ServerId,
    Guid ChannelId);