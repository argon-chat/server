namespace Argon.Features.Scheduling;

/// <summary>
/// A scheduled background task executed via NATS WorkQueue.
/// Only one DC processes each scheduled invocation (first to ack wins).
/// </summary>
public interface IScheduledTask
{
    /// <summary>
    /// Unique task name used as NATS subject suffix: argon.schedules.{TaskName}
    /// </summary>
    string TaskName { get; }

    /// <summary>
    /// How often this task should fire.
    /// </summary>
    TimeSpan Interval { get; }

    /// <summary>
    /// Initial delay before first execution.
    /// </summary>
    TimeSpan InitialDelay { get; }

    /// <summary>
    /// Execute the task. Called at most once per interval across all DCs.
    /// </summary>
    Task ExecuteAsync(CancellationToken ct);
}
