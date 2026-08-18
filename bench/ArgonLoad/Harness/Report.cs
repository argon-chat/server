namespace Argon.Load.Harness;

public static class Report
{
    public static void Print(string title, TimeSpan wall, IReadOnlyList<Measurement> measurements)
    {
        var rows = measurements.Select(m => m.Take()).Where(s => s.Count > 0 || s.Failed > 0).ToArray();

        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('─', 92));
        Console.WriteLine($"{"step",-34}{"n",6}{"fail",6}{"p50",9}{"p95",9}{"p99",9}{"max",9}{"mean",9}");
        Console.WriteLine(new string('─', 92));

        foreach (var s in rows)
            Console.WriteLine(
                $"{s.Name,-34}{s.Count,6}{s.Failed,6}{s.P50,9:F1}{s.P95,9:F1}{s.P99,9:F1}{s.Max,9:F1}{s.Mean,9:F1}");

        Console.WriteLine(new string('─', 92));
        Console.WriteLine($"wall clock: {wall.TotalSeconds:F2}s   (all times in ms)");

        var failed = rows.Sum(r => r.Failed);
        if (failed > 0)
            Console.WriteLine($"{failed} call(s) failed — the numbers above describe only the ones that did not");
    }
}
