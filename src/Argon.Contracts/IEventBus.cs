namespace Argon;

using Streaming;

[TsInterface]
public interface IEventBus : IArgonService
{
    Task<IArgonStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId);
}