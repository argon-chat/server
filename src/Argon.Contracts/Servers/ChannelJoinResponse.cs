namespace Argon;

[TsInterface, MessagePackObject(true)]
public sealed record ChannelJoinResponse(
    string Token);