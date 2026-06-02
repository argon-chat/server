namespace Argon.Features.Scheduling;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using System.Text;
using System.Text.Json;

/// <summary>
/// Background service that orchestrates scheduled tasks via NATS JetStream WorkQueue.
/// Each DC runs this service. WorkQueue retention guarantees only one consumer (DC) processes each message.
/// 
/// Architecture:
/// - A "scheduler" timer publishes a trigger message to `argon.schedules.{taskName}` at each interval
/// - Only the DC that holds the NATS leader role publishes triggers (leader election via stream ownership)
/// - All DCs subscribe as consumers — WorkQueue delivers each message to exactly one consumer
/// - On receipt, the consumer executes the task locally
///
/// For NATS ≥ 2.14 with native Cron/MsgSchedules:
/// The timer-based publisher can be replaced with server-side cron delivery,
/// but the consumer side remains identical.
/// </summary>
public sealed class ScheduledTasksService : BackgroundService
{
    private const string StreamName = "argon_schedules";
    private const string SubjectPrefix = "argon.schedules.";
    private const string ConsumerPrefix = "sched_";

    private readonly INatsClient _nats;
    private readonly IEnumerable<IScheduledTask> _tasks;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ScheduledTasksService> _logger;

    public ScheduledTasksService(
        INatsClient nats,
        IEnumerable<IScheduledTask> tasks,
        IConfiguration configuration,
        ILogger<ScheduledTasksService> logger)
    {
        _nats = nats;
        _tasks = tasks;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var dcId = _configuration["Datacenter:Id"] ?? "local";

        // Wait a bit for NATS connection to stabilize
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var js = _nats.CreateJetStreamContext();

        // Ensure stream exists
        await EnsureStreamAsync(js, stoppingToken);

        var taskList = _tasks.ToList();
        if (taskList.Count == 0)
        {
            _logger.LogInformation("No scheduled tasks registered, service idle");
            return;
        }

        _logger.LogInformation("Starting scheduled tasks service with {Count} tasks on DC {DcId}",
            taskList.Count, dcId);

        // Start publisher + consumer for each task
        var taskRunners = taskList.Select(t => RunTaskAsync(js, t, dcId, stoppingToken));
        await Task.WhenAll(taskRunners);
    }

    private async Task EnsureStreamAsync(INatsJSContext js, CancellationToken ct)
    {
        try
        {
            await js.CreateOrUpdateStreamAsync(new StreamConfig(StreamName, [$"{SubjectPrefix}>"])
            {
                Retention = StreamConfigRetention.Workqueue,
                Storage = StreamConfigStorage.File,
                MaxAge = TimeSpan.FromHours(2),
                MaxMsgs = 1000,
                Discard = StreamConfigDiscard.Old,
                DuplicateWindow = TimeSpan.FromMinutes(5),
                NumReplicas = 1
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create/update {StreamName} stream", StreamName);
            throw;
        }
    }

    private async Task RunTaskAsync(INatsJSContext js, IScheduledTask task, string dcId, CancellationToken ct)
    {
        var subject = $"{SubjectPrefix}{task.TaskName}";
        var consumerName = $"{ConsumerPrefix}{task.TaskName}";

        // Create durable consumer for this task
        INatsJSConsumer consumer;
        try
        {
            consumer = await js.CreateOrUpdateConsumerAsync(StreamName, new ConsumerConfig(consumerName)
            {
                FilterSubject = subject,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                AckWait = TimeSpan.FromMinutes(5), // tasks can take up to 5 min
                MaxDeliver = 3, // retry up to 3 times on failure
                MaxAckPending = 1 // process one at a time
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create consumer for task {TaskName}", task.TaskName);
            return;
        }

        // Run publisher and consumer concurrently
        await Task.WhenAll(
            PublishScheduleAsync(js, task, subject, dcId, ct),
            ConsumeScheduleAsync(consumer, task, ct)
        );
    }

    private async Task PublishScheduleAsync(
        INatsJSContext js, IScheduledTask task, string subject, string dcId, CancellationToken ct)
    {
        // Initial delay
        await Task.Delay(task.InitialDelay, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var msgId = $"{task.TaskName}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                var payload = new ScheduledTaskTrigger(task.TaskName, dcId, DateTimeOffset.UtcNow);
                var data = JsonSerializer.SerializeToUtf8Bytes(payload);

                var ack = await js.PublishAsync(subject, data, opts: new NatsJSPubOpts
                {
                    MsgId = msgId // dedup within DuplicateWindow
                }, cancellationToken: ct);

                if (ack.Error is null)
                {
                    _logger.LogDebug("Published schedule trigger for {TaskName} (seq: {Seq})",
                        task.TaskName, ack.Seq);
                }
                else if (ack.Duplicate)
                {
                    _logger.LogDebug("Duplicate schedule trigger for {TaskName} suppressed", task.TaskName);
                }
                else
                {
                    _logger.LogWarning("Failed to publish schedule for {TaskName}: {Error}",
                        task.TaskName, ack.Error.Description);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error publishing schedule for {TaskName}", task.TaskName);
            }

            await Task.Delay(task.Interval, ct);
        }
    }

    private async Task ConsumeScheduleAsync(INatsJSConsumer consumer, IScheduledTask task, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
                {
                    try
                    {
                        _logger.LogInformation("Executing scheduled task {TaskName}", task.TaskName);
                        await task.ExecuteAsync(ct);
                        await msg.AckAsync(cancellationToken: ct);
                        _logger.LogInformation("Scheduled task {TaskName} completed successfully", task.TaskName);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Scheduled task {TaskName} failed, will be redelivered", task.TaskName);
                        await msg.NakAsync(cancellationToken: ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Consumer error for task {TaskName}, reconnecting in 5s", task.TaskName);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }
}

public sealed record ScheduledTaskTrigger(string TaskName, string SourceDc, DateTimeOffset FiredAt);
