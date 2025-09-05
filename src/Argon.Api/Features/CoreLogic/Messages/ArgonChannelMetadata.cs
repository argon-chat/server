namespace Argon.Api.Features.CoreLogic.Messages;

public class ArgonChannelMetadata
{
    public Guid ServerId { get; set; }
    public Guid ChannelId { get; set; }
    public ulong LastMessageId { get; set; }
}