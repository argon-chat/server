namespace Argon.Features;

public class AsyncContainer<T>(Func<Task<T>> taskFactory)
    where T : class
{
    private readonly Task<T> _lazyTask = taskFactory();
    private volatile T?      _value;

    public T Value => _value ?? throw new Exception($"Not created");

    public bool IsValueCreated => _value is not null;

    public async ValueTask DoCreateAsync()
        => Interlocked.Exchange(ref _value, await _lazyTask);
}