namespace Argon.Cassandra.Core;

/// <summary>
/// Metrics for a specific operation type and table combination
/// </summary>
public class OperationMetrics(string operationType, string tableName)
{
    private readonly Lock           guarder          = new();
    private readonly List<TimeSpan> responseTimes = new();
    private readonly List<string>   recentErrors  = new();

    public string   OperationType     { get; } = operationType;
    public string   TableName         { get; } = tableName;
    public long     TotalCalls        { get; private set; }
    public long     SuccessfulCalls   { get; private set; }
    public long     FailedCalls       { get; private set; }
    public TimeSpan MinResponseTime   { get; private set; } = TimeSpan.MaxValue;
    public TimeSpan MaxResponseTime   { get; private set; } = TimeSpan.Zero;
    public TimeSpan TotalResponseTime { get; private set; } = TimeSpan.Zero;

    public OperationMetrics AddRecord(TimeSpan duration, bool success, string? error = null)
    {
        lock (guarder)
        {
            TotalCalls++;

            if (success)
            {
                SuccessfulCalls++;
            }
            else
            {
                FailedCalls++;
                if (!string.IsNullOrEmpty(error))
                {
                    recentErrors.Add(error);
                    // Keep only recent errors
                    if (recentErrors.Count > 10)
                    {
                        recentErrors.RemoveAt(0);
                    }
                }
            }

            responseTimes.Add(duration);
            if (responseTimes.Count > 1000) // Keep last 1000 response times
            {
                responseTimes.RemoveAt(0);
            }

            TotalResponseTime = TotalResponseTime.Add(duration);

            if (duration < MinResponseTime)
                MinResponseTime = duration;

            if (duration > MaxResponseTime)
                MaxResponseTime = duration;
        }

        return this;
    }

    public TimeSpan AverageResponseTime => TotalCalls > 0
        ? TimeSpan.FromTicks(TotalResponseTime.Ticks / TotalCalls)
        : TimeSpan.Zero;

    public double SuccessRate => TotalCalls > 0 ? (double)SuccessfulCalls / TotalCalls : 1.0;

    public TimeSpan MedianResponseTime
    {
        get
        {
            lock (guarder)
            {
                if (!responseTimes.Any()) return TimeSpan.Zero;

                var sorted = responseTimes.OrderBy(t => t.Ticks).ToList();
                var mid    = sorted.Count / 2;

                return sorted.Count % 2 == 0
                    ? TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2)
                    : sorted[mid];
            }
        }
    }

    public TimeSpan P95ResponseTime
    {
        get
        {
            lock (guarder)
            {
                if (!responseTimes.Any()) return TimeSpan.Zero;

                var sorted = responseTimes.OrderBy(t => t.Ticks).ToList();
                var index  = (int)Math.Ceiling(sorted.Count * 0.95) - 1;

                return sorted[Math.Max(0, index)];
            }
        }
    }

    public List<string> RecentErrors
    {
        get
        {
            lock (guarder)
            {
                return recentErrors.ToList();
            }
        }
    }
}