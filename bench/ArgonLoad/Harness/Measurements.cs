namespace Argon.Load.Harness;

using System.Diagnostics;

/// <summary>
/// One timed thing that happened, kept as raw samples rather than a running aggregate.
/// </summary>
/// <remarks>
/// Percentiles are the point of a load test and cannot be recovered from an average, so every
/// sample is retained. At the sizes this harness runs — thousands of samples, not millions — that is
/// a few hundred kilobytes and buys exact quantiles instead of estimated ones.
/// </remarks>
public sealed class Measurement(string name)
{
    private readonly List<double> samples = [];
    private readonly Lock         gate    = new();

    public string Name    => name;
    public int    Failed  { get; private set; }

    public void Record(TimeSpan elapsed)
    {
        lock (gate)
            samples.Add(elapsed.TotalMilliseconds);
    }

    public void Fail()
    {
        lock (gate)
            Failed++;
    }

    public async Task<T> TimeAsync<T>(Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await action();
            Record(Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch
        {
            Fail();
            throw;
        }
    }

    public Snapshot Take()
    {
        lock (gate)
        {
            var ordered = samples.Order().ToArray();

            return new Snapshot
            {
                Name   = name,
                Count  = ordered.Length,
                Failed = Failed,
                Min    = Quantile(ordered, 0),
                P50    = Quantile(ordered, 0.50),
                P95    = Quantile(ordered, 0.95),
                P99    = Quantile(ordered, 0.99),
                Max    = Quantile(ordered, 1),
                Mean   = ordered.Length == 0 ? 0 : ordered.Average()
            };
        }
    }

    /// <summary>Nearest-rank on the sorted samples — no interpolation, so every number printed is one that happened.</summary>
    private static double Quantile(double[] ordered, double q)
    {
        if (ordered.Length == 0)
            return 0;

        var index = (int)Math.Ceiling(q * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }
}

public sealed record Snapshot
{
    public required string Name   { get; init; }
    public required int    Count  { get; init; }
    public required int    Failed { get; init; }
    public required double Min    { get; init; }
    public required double P50    { get; init; }
    public required double P95    { get; init; }
    public required double P99    { get; init; }
    public required double Max    { get; init; }
    public required double Mean   { get; init; }
}
