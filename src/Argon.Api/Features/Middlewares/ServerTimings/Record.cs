namespace Argon.Features.Middlewares;

using System.Diagnostics;

public sealed class Record
{
    private readonly double?   duration;
    private readonly Stopwatch stopwatch;

    public Record(string name, string? description, double? duration = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is not valid.", nameof(name));
        }

        Description = description?.Trim();
        Name        = name.Trim();

        this.duration = duration;

        if (!duration.HasValue)
            stopwatch = Stopwatch.StartNew();
    }

    public string? Description { get; }

    public double? Duration => duration ?? stopwatch?.ElapsedMilliseconds;

    public string Name { get; }

    public void Finish()
    {
        if (IsFinished())
            return;
        stopwatch.Stop();
    }

    public bool IsFinished() => stopwatch?.IsRunning != true;
}