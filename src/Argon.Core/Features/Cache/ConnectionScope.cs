namespace Argon.Services;

using StackExchange.Redis;

public readonly struct ConnectionScope(IConnectionMultiplexer multiplexer, RedisConnectionPool pool) : IDisposable
{
    void IDisposable.Dispose()
        => pool.Return(multiplexer);

    public IServer GetServer()
        => multiplexer.GetServer(multiplexer.GetEndPoints().First());

    public IConnectionMultiplexer GetMultiplexer() 
        => multiplexer;

    public IDatabase GetDatabase(int dbId = -1)
        => multiplexer.GetDatabase(dbId);
}