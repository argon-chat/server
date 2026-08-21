namespace Argon.Extensions;

using System.Runtime.CompilerServices;

public class AsyncLazy<T>
{
    private readonly Lazy<Task<T>> _lazyTask;

    public AsyncLazy(Func<Task<T>> taskFactory)
    {
        if (taskFactory == null)
            throw new ArgumentNullException(nameof(taskFactory));

        _lazyTask = new Lazy<Task<T>>(() => Task.Run(taskFactory));
    }

    public Task<T> Value => _lazyTask.Value;

    public bool IsValueCreated => _lazyTask.IsValueCreated;

    public TaskAwaiter<T> GetAwaiter() => Value.GetAwaiter();
}