namespace Argon.Streaming.Events;

using Servers;

[TsInterface, MessagePackObject(true)]
public record CreateChannelRequest(Guid serverId, string name, ChannelType kind, string desc);