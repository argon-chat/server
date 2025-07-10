namespace Argon.Cassandra.Features.Messages;

public class ArgonMessageDeduplication
{
    public Guid  ServerId  { get; set; }
    public Guid  ChannelId { get; set; }
    public ulong RandomId { get; set; }
    public ulong MessageId { get; set; }
}