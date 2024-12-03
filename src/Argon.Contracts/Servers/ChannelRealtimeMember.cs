namespace Argon;

[TsInterface, MessagePackObject(true)]
public record ChannelRealtimeMember(Guid UserId);