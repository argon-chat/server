namespace Argon.Services;

using StackExchange.Redis;

public struct ConnectionScope(IConnectionMultiplexer connection, RedisConnectionPool pool) : IAsyncDisposable, IDisposable
{
    private int  disposed = 0;
    private bool faulted  = false;

    public IConnectionMultiplexer Connection => connection;

    public IDatabase GetDatabase(int index = 0) => connection.GetDatabase(index);

    public IServer GetServer() => connection.GetServer(connection.GetEndPoints().First());

    public void MarkFaulted() => faulted = true;

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        if (faulted)
        {
            await pool.ReturnFaultedAsync(connection);
        }
        else
        {
            pool.Return(connection);
        }
    }
}