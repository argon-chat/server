namespace Argon.Services;

using StackExchange.Redis;

public class CacheSubscriber(RedisChannel channelKey, ISubscriber subscriber, ConnectionScope scope) : IDisposable, IAsyncDisposable
{
    public void Dispose()
    {
        subscriber.Unsubscribe(channelKey);
        ((IDisposable)scope).Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await subscriber.UnsubscribeAsync(channelKey);
        ((IDisposable)scope).Dispose();
    }
}