namespace Argon.Load.Harness;

using System.Diagnostics;

/// <summary>
/// Runs N copies of the same work and starts them together.
/// </summary>
/// <remarks>
/// A herd, not a ramp. The question this harness was built for is what happens when a crowd arrives
/// at once — a deploy finishing, a region reconnecting, everyone opening the app in the morning —
/// and that is a different question from steady-state throughput. Ramping hides it: spread the same
/// arrivals over a minute and the queue never forms.
/// <para>
/// Every worker is constructed and warmed before the barrier releases, so the measurement covers the
/// work and not the cost of creating a client.
/// </para>
/// </remarks>
public static class Herd
{
    /// <param name="settle">
    /// Runs for every worker after all of them are prepared and before any is released. This is where
    /// a scenario puts work that has to see the finished world — a client that arrives already
    /// holding the space cannot capture what it holds until the last member has joined.
    /// </param>
    public static async Task<TimeSpan> RunAsync<TWorker>(
        int                                count,
        Func<int, CancellationToken, Task<TWorker>> prepare,
        Func<TWorker, CancellationToken, Task>      run,
        CancellationToken                           ct,
        Func<TWorker, CancellationToken, Task>?     settle = null)
    {
        var ready   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new TWorker[count];

        Console.WriteLine($"preparing {count} client(s)…");

        // Preparation is deliberately sequential-ish: registering the users is itself load, and
        // doing it in a burst would measure the registration path instead of the one under test.
        await Parallel.ForAsync(0, count,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, token) => workers[i] = await prepare(i, token));

        if (settle is not null)
        {
            Console.WriteLine($"settling {count} client(s)…");

            await Parallel.ForAsync(0, count,
                new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
                async (i, token) => await settle(workers[i], token));
        }

        Console.WriteLine($"releasing {count} client(s) at once…");

        var started = Stopwatch.GetTimestamp();

        var flight = workers.Select(async worker =>
        {
            await ready.Task;
            try
            {
                await run(worker, ct);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"  worker failed: {e.Message}");
            }
        }).ToArray();

        ready.SetResult();
        await Task.WhenAll(flight);

        return Stopwatch.GetElapsedTime(started);
    }
}
