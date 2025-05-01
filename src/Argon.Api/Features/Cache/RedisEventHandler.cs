namespace Argon.Services;

using MessagePipe;
using R3;
using StackExchange.Redis;

public class RedisEventHandler(IRedisPoolConnections pool, IAsyncPublisher<OnRedisKeyExpired> publisher, IHostApplicationLifetime hostedLifecycle, ILogger<RedisEventHandler> logger, IRedisEventStorage eventStorage) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var scope = pool.Rent();

        var k = new RedisChannel("__keyevent@0__:expired", RedisChannel.PatternMode.Auto);
        var s = scope.GetMultiplexer().GetSubscriber();
        var c = new CacheSubscriber(k, s, scope);
        var w = await s.SubscribeAsync(k);
        logger.LogInformation("Registered event handler subscription for '__keyevent@0__:expired'");
        w.OnMessage(async message =>
        {
            if (eventStorage is RedisEventStorage ev)
            {
                logger.LogInformation("Key expired: {key}", message.Message.ToString());
                ev.OnKeyExpired.OnNext(new OnRedisKeyExpired(message.Message.ToString()));
            } else logger.LogError("redis event storage incorrect type");
        });
        hostedLifecycle.ApplicationStopping.Register(() => c.Dispose());
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public class RedisEventStorage : IRedisEventStorage
{
    public Subject<OnRedisKeyExpired> OnKeyExpired { get; } = new();


    public IDisposable OnKeyExpiredSubscribeAsync(Func<OnRedisKeyExpired, CancellationToken, ValueTask> @event)
        => OnKeyExpired.SubscribeAwait(@event);
}

public interface IRedisEventStorage
{
    IDisposable OnKeyExpiredSubscribeAsync(Func<OnRedisKeyExpired, CancellationToken, ValueTask> @event);
}