namespace Argon.Api.Features.CoreLogic.Messages;

public record ArgonMessageCounters
{
    public ulong NextMessageId { get; set; }
    public Guid  SpaceId      { get; set; }
    public Guid  ChannelId     { get; set; }
}