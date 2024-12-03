namespace Argon;

using Streaming;

[TsInterface]
public interface IEventBus : IArgonService
{
    ValueTask<IArgonStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId);
}