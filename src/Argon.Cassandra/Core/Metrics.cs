using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Argon.Cassandra.Core;

/// <summary>
/// Provides performance monitoring and metrics collection for Cassandra operations
/// </summary>
public class CassandraMetrics(ILogger? logger = null)
{
    private readonly ConcurrentDictionary<string, OperationMetrics> _operationMetrics = new();
    private readonly ConcurrentQueue<OperationRecord>               _recentOperations = new();
    private          long                                           _totalOperations  = 0;

    /// <summary>
    /// Records the execution of an operation
    /// </summary>
    /// <param name="operationType">Type of operation (e.g., "SELECT", "INSERT", "UPDATE")</param>
    /// <param name="tableName">Name of the table involved</param>
    /// <param name="duration">Execution duration</param>
    /// <param name="success">Whether the operation succeeded</param>
    /// <param name="error">Error message if operation failed</param>
    public void RecordOperation(string operationType, string tableName, TimeSpan duration, bool success, string? error = null)
    {
        var key = $"{operationType}:{tableName}";
        
        _operationMetrics.AddOrUpdate(key, 
            new OperationMetrics(operationType, tableName),
            (_, existing) => existing.AddRecord(duration, success, error));

        // Keep recent operations for analysis (limit to last 1000)
        _recentOperations.Enqueue(new OperationRecord
        {
            OperationType = operationType,
            TableName = tableName,
            Duration = duration,
            Success = success,
            Error = error,
            Timestamp = DateTime.UtcNow
        });

        // Maintain size limit
        while (_recentOperations.Count > 1000)
        {
            _recentOperations.TryDequeue(out _);
        }

        Interlocked.Increment(ref _totalOperations);

        // Log slow operations
        if (duration.TotalMilliseconds > 1000)
        {
            logger?.LogWarning("Slow operation detected: {Operation} on {Table} took {Duration}ms", 
                operationType, tableName, duration.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Gets performance summary for all operations
    /// </summary>
    public PerformanceSummary GetSummary()
    {
        var allMetrics = _operationMetrics.Values.ToList();
        var recentOps = _recentOperations.ToList();

        return new PerformanceSummary
        {
            TotalOperations = _totalOperations,
            OperationMetrics = allMetrics.ToDictionary(m => $"{m.OperationType}:{m.TableName}", m => m),
            RecentOperations = recentOps.TakeLast(100).ToList(),
            AverageResponseTime = allMetrics.Any() ? TimeSpan.FromMilliseconds(
                allMetrics.Average(m => m.AverageResponseTime.TotalMilliseconds)) : TimeSpan.Zero,
            SuccessRate = allMetrics.Any() ? 
                allMetrics.Average(m => m.SuccessRate) : 1.0,
            SlowestOperations = recentOps
                .OrderByDescending(op => op.Duration)
                .Take(10)
                .ToList()
        };
    }

    /// <summary>
    /// Gets metrics for a specific operation type and table
    /// </summary>
    /// <param name="operationType">Type of operation</param>
    /// <param name="tableName">Table name</param>
    /// <returns>Metrics for the specified operation, or null if not found</returns>
    public OperationMetrics? GetOperationMetrics(string operationType, string tableName)
    {
        var key = $"{operationType}:{tableName}";
        return _operationMetrics.TryGetValue(key, out var metrics) ? metrics : null;
    }

    /// <summary>
    /// Resets all collected metrics
    /// </summary>
    public void Reset()
    {
        _operationMetrics.Clear();
        while (_recentOperations.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _totalOperations, 0);
        
        logger?.LogInformation("Cassandra metrics have been reset");
    }

    /// <summary>
    /// Logs current performance summary
    /// </summary>
    public void LogSummary()
    {
        var summary = GetSummary();
        
        logger?.LogInformation(
            "Cassandra Performance Summary - Total Ops: {TotalOps}, Avg Response: {AvgResponse}ms, Success Rate: {SuccessRate:P2}",
            summary.TotalOperations,
            summary.AverageResponseTime.TotalMilliseconds,
            summary.SuccessRate);

        if (summary.SlowestOperations.Any())
        {
            var slowest = summary.SlowestOperations.First();
            logger?.LogInformation("Slowest recent operation: {Operation} on {Table} ({Duration}ms)",
                slowest.OperationType, slowest.TableName, slowest.Duration.TotalMilliseconds);
        }
    }
}

/// <summary>
/// Metrics for a specific operation type and table combination
/// </summary>
public class OperationMetrics(string operationType, string tableName)
{
    private readonly object _lock = new();
    private readonly List<TimeSpan> _responseTimes = new();
    private readonly List<string> _recentErrors = new();

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
        lock (_lock)
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
                    _recentErrors.Add(error);
                    // Keep only recent errors
                    if (_recentErrors.Count > 10)
                    {
                        _recentErrors.RemoveAt(0);
                    }
                }
            }

            _responseTimes.Add(duration);
            if (_responseTimes.Count > 1000) // Keep last 1000 response times
            {
                _responseTimes.RemoveAt(0);
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
            lock (_lock)
            {
                if (!_responseTimes.Any()) return TimeSpan.Zero;
                
                var sorted = _responseTimes.OrderBy(t => t.Ticks).ToList();
                var mid = sorted.Count / 2;
                
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
            lock (_lock)
            {
                if (!_responseTimes.Any()) return TimeSpan.Zero;
                
                var sorted = _responseTimes.OrderBy(t => t.Ticks).ToList();
                var index = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
                
                return sorted[Math.Max(0, index)];
            }
        }
    }

    public List<string> RecentErrors
    {
        get
        {
            lock (_lock)
            {
                return _recentErrors.ToList();
            }
        }
    }
}

/// <summary>
/// Record of a single operation
/// </summary>
public class OperationRecord
{
    public string OperationType { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Overall performance summary
/// </summary>
public class PerformanceSummary
{
    public long TotalOperations { get; set; }
    public Dictionary<string, OperationMetrics> OperationMetrics { get; set; } = new();
    public List<OperationRecord> RecentOperations { get; set; } = new();
    public TimeSpan AverageResponseTime { get; set; }
    public double SuccessRate { get; set; }
    public List<OperationRecord> SlowestOperations { get; set; } = new();
}

/// <summary>
/// Stopwatch helper for measuring operation duration
/// </summary>
public class OperationTimer(CassandraMetrics metrics, string operationType, string tableName) : IDisposable
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private          bool      _success   = true;
    private          string?   _error;

    public void MarkFailure(string error)
    {
        _success = false;
        _error = error;
    }

    public void Dispose()
    {
        _stopwatch.Stop();
        metrics.RecordOperation(operationType, tableName, _stopwatch.Elapsed, _success, _error);
    }
}
