namespace Argon.Api.Features.Utils;

using System.Runtime.CompilerServices;
using System.Threading;

public static class AsyncEnumerableEx
{
    public async static IAsyncEnumerable<TSource> MergeAsync<TSource>(this IAsyncEnumerable<TSource>[] sources, CancellationToken ct = default)
    {
        if (sources == null)
            throw new ArgumentNullException(nameof(sources));

        var count         = sources.Length;
        var enumerators   = new IAsyncEnumerator<TSource>?[count];
        var moveNextTasks = new ValueTask<bool>[count];

        try
        {
            for (var i = 0; i < count; i++)
            {
                var enumerator = sources[i].GetAsyncEnumerator(ct);
                enumerators[i]   = enumerator;
                moveNextTasks[i] = enumerator.MoveNextAsync();
            }

            var whenAny = WhenAny(moveNextTasks);

            var active = count;

            while (active > 0)
            {
                var index = await whenAny;

                var enumerator   = enumerators[index];
                var moveNextTask = moveNextTasks[index];

                if (!await moveNextTask.ConfigureAwait(false))
                {
                    moveNextTasks[index] = new ValueTask<bool>();
                    enumerators[index]   = null;
                    await enumerator!.DisposeAsync().ConfigureAwait(false);

                    active--;
                }
                else
                {
                    var item = enumerator!.Current;
                    whenAny.Replace(index, enumerator.MoveNextAsync());

                    yield return item;
                }
            }
        }
        finally
        {
            var errors = default(List<Exception>);

            for (var i = count - 1; i >= 0; i--)
            {
                var moveNextTask = moveNextTasks[i];
                var enumerator   = enumerators[i];

                try
                {
                    try
                    {
                        _ = await moveNextTask.ConfigureAwait(false);
                    }
                    finally
                    {
                        if (enumerator != null)
                        {
                            await enumerator.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors ??= [];

                    errors.Add(ex);
                }
            }

            if (errors != null)
                throw new AggregateException(errors);
        }
    }


    internal static WhenAnyValueTask<T> WhenAny<T>(ValueTask<T>[] tasks)
    {
        var whenAny = new WhenAnyValueTask<T>(tasks);

        whenAny.Start();

        return whenAny;
    }

    internal sealed class WhenAnyValueTask<T>
    {
        private readonly ValueTask<T>[] tasks;
        private readonly Action[] onReady;

        private readonly int[] ready;
        private int head;
        private int tail;

        private Action? continuation;

        public WhenAnyValueTask(ValueTask<T>[] tasks)
        {
            this.tasks = tasks;

            var n = tasks.Length;
            ready = new int[n];
            onReady = new Action[n];

            for (var i = 0; i < n; i++)
            {
                var index = i;
                onReady[index] = () => OnReady(index);
            }
        }

        public void Start()
        {
            for (var i = 0; i < tasks.Length; i++)
                tasks[i].ConfigureAwait(false).GetAwaiter().OnCompleted(onReady[i]);
        }

        public void Replace(int index, in ValueTask<T> task)
        {
            Debug.Assert(tasks[index].IsCompleted, "Task must be completed before replacement.");

            tasks[index] = task;
            task.ConfigureAwait(false).GetAwaiter().OnCompleted(onReady[index]);
        }

        private void OnReady(int index)
        {
            var pos = tail;
            ready[pos] = index;
            tail = (tail + 1) % ready.Length;
            var cont = Interlocked.Exchange(ref continuation, null);
            cont?.Invoke();
        }

        private bool IsCompleted()
            => Volatile.Read(ref head) != Volatile.Read(ref tail);

        private int GetResult()
        {
            var pos = head;
            head = (head + 1) % ready.Length;
            return ready[pos];
        }

        private void OnCompleted(Action action)
        {
            if (IsCompleted())
            {
                action();
                return;
            }

            if (Interlocked.CompareExchange(ref continuation, action, null) != null)
                throw new InvalidOperationException("Only one awaiter is allowed.");

            if (!IsCompleted()) return;
            var cont = Interlocked.Exchange(ref continuation, null);
            cont?.Invoke();
        }

        public Awaiter GetAwaiter() => new(this);

        public readonly struct Awaiter(WhenAnyValueTask<T> parent) : INotifyCompletion
        {
            public bool IsCompleted => parent.IsCompleted();
            public int GetResult() => parent.GetResult();
            public void OnCompleted(Action action) => parent.OnCompleted(action);
        }
    }
}