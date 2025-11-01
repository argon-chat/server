namespace Argon.Api.Features.CoreLogic.Messages;

public class ArgonChannelMessageCounter
{
    public Guid SpaceId   { get; set; }
    public Guid ChannelId { get; set; }
    public long Value   { get; set; }
}