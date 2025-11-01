namespace Argon.Api.Features.CoreLogic.Messages;

public class ArgonMessageDeduplication
{
    public Guid spaceId   { get; set; }
    public Guid ChannelId { get; set; }
    public long RandomId  { get; set; }
    public long  MessageId { get; set; }
}