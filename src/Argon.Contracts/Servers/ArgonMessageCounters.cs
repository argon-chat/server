namespace Argon;

public record ArgonMessageCounters
{
    public ulong NextMessageId { get; set; }
    public Guid  ServerId      { get; set; }
    public Guid  ChannelId     { get; set; }
}