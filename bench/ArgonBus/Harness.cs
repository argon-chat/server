namespace Argon.Bus;

using System.Buffers.Binary;
using System.Diagnostics;

/// <summary>
/// One timed thing that happened, kept as raw samples rather than a running aggregate.
/// </summary>
/// <remarks>
/// Deliberately a copy of the one in <c>bench/ArgonLoad</c> rather than a shared reference: that
/// project pulls in <c>Argon.Core</c> and everything behind it, and this one has to stay a
/// standalone tool that builds and runs without the server.
/// </remarks>
public sealed class Measurement(string name)
{
    private readonly List<double> samples = [];
    private readonly Lock         gate    = new();

    public string Name   => name;
    public int    Failed { get; private set; }

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
                P50    = Quantile(ordered, 0.50),
                P95    = Quantile(ordered, 0.95),
                P99    = Quantile(ordered, 0.99),
                Max    = Quantile(ordered, 1),
                Mean   = ordered.Length == 0 ? 0 : ordered.Average()
            };
        }
    }

    /// <summary>Nearest-rank on the sorted samples — every number printed is one that happened.</summary>
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
    public required double P50    { get; init; }
    public required double P95    { get; init; }
    public required double P99    { get; init; }
    public required double Max    { get; init; }
    public required double Mean   { get; init; }
}

public static class Report
{
    public static void Print(string title, TimeSpan wall, IReadOnlyList<Measurement> measurements)
    {
        var rows = measurements.Select(m => m.Take()).Where(s => s.Count > 0 || s.Failed > 0).ToArray();

        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', 86));
        Console.WriteLine($"{"step",-36}{"n",8}{"fail",6}{"p50",9}{"p95",9}{"p99",9}{"max",9}");
        Console.WriteLine(new string('-', 86));

        foreach (var s in rows)
            Console.WriteLine($"{s.Name,-36}{s.Count,8}{s.Failed,6}{s.P50,9:F2}{s.P95,9:F2}{s.P99,9:F2}{s.Max,9:F2}");

        Console.WriteLine(new string('-', 86));
        Console.WriteLine($"wall clock: {wall.TotalSeconds:F2}s   (all times in ms)");
    }
}

/// <summary>
/// The thing that travels: an id, the timestamp it was published at, and filler to whatever size
/// the run is modelling.
/// </summary>
/// <remarks>
/// The timestamp rides inside the payload rather than in a side table because the only clock that
/// can time a hop across four different transports is the one both ends already share — this whole
/// bench is one process, which is also why the absolute numbers are a floor rather than a forecast.
/// A side table would work too and would cost a dictionary lookup per delivery; at a hundred
/// thousand deliveries a second that is not free, and it is not the thing being measured.
/// </remarks>
public static class Probe
{
    public const int HeaderSize = 16;

    public static byte[] Create(long id, int size)
    {
        var buffer = new byte[Math.Max(size, HeaderSize)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, id);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), Stopwatch.GetTimestamp());
        return buffer;
    }

    public static (long Id, long SentAt) Read(ReadOnlySpan<byte> payload)
        => (BinaryPrimitives.ReadInt64LittleEndian(payload),
            BinaryPrimitives.ReadInt64LittleEndian(payload[8..]));

    /// <summary>Send-to-here for a payload, read from the payload itself.</summary>
    public static TimeSpan Age(ReadOnlySpan<byte> payload)
        => Stopwatch.GetElapsedTime(BinaryPrimitives.ReadInt64LittleEndian(payload[8..]));
}
