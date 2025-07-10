namespace Argon.Cassandra.Features.Messages;

public class ArgonChannelMetadata
{
    public Guid ServerId { get; set; }
    public Guid ChannelId { get; set; }
    public ulong LastMessageId { get; set; }
}