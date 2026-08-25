namespace Argon.Api.Features.CoreLogic.Messages;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

/// <summary>
/// Batches message inserts so that a channel's throughput is not one database round trip wide.
/// </summary>
/// <remarks>
/// Every send used to open its own context and insert its own row, and a single-row insert into
/// CockroachDB is a distributed write — about two milliseconds. The insert costs the same whether it
/// carries one row or two hundred, so a node under load pays once for what it used to pay for per
/// message.
/// <para>
/// The turn now mints the id and hands the row over. Ids come from the snowflake generator rather
/// than the <c>unique_rowid()</c> column default, because waiting for the database to tell us the id
/// is exactly what could not be waited for.
/// </para>
/// <para>
/// Senders wait for their batch to commit, so a successful send still means a stored message. The
/// speed does not come from skipping the wait — it comes from a hundred senders sharing one round
/// trip instead of buying one each. Returning early was measured and is faster still, and was not
/// kept: the ceiling it bought is far above what a channel needs, and it would have paid for that
/// with messages lost whenever a silo died inside the window.
/// </para>
/// </remarks>
public sealed class MessageWriteBuffer(
    IDbContextFactory<ApplicationDbContext> context,
    MessageDeduplicationService deduplication,
    IOptions<MessagesOptions> options,
    ILogger<MessageWriteBuffer> logger) : BackgroundService
{
    /// <summary>
    /// Rows per insert. Past a few hundred the statement itself starts to dominate and the tail of
    /// the batch waits longer than the window it was supposed to save.
    /// </summary>
    private const int MaxBatch = 256;

    private readonly Channel<Pending> queue = Channel.CreateUnbounded<Pending>(
        new UnboundedChannelOptions { SingleReader = false });

    private sealed record Pending(ArgonMessageEntity? Message, long RandomId, TaskCompletionSource Committed);

    /// <summary>
    /// Queues a row that already has its id. The returned task completes when it is committed —
    /// callers that only need the id can ignore it, which is the point.
    /// </summary>
    public Task EnqueueAsync(ArgonMessageEntity message, long randomId)
    {
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!queue.Writer.TryWrite(new Pending(message, randomId, committed)))
            return Task.FromException(new InvalidOperationException("the message write buffer is closed"));

        return committed.Task;
    }

    /// <summary>
    /// Completes once everything queued before the call is committed.
    /// </summary>
    /// <remarks>
    /// A barrier rather than a flag, because the reader drains in order: when the barrier lands,
    /// everything ahead of it already has. Reads do not need it — a send does not return until its
    /// own row is committed — but anything that wants to observe the queue draining does.
    /// </remarks>
    public Task FlushAsync()
    {
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        return queue.Writer.TryWrite(new Pending(null, 0, committed))
            ? committed.Task
            : Task.CompletedTask;
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
        => await Task.WhenAll(Enumerable.Range(0, options.Value.WriteConcurrency)
           .Select(_ => DrainAsync(stoppingToken)));

    private async Task DrainAsync(CancellationToken stoppingToken)
    {
        var batch = new List<Pending>(MaxBatch);

        while (await queue.Reader.WaitToReadAsync(stoppingToken))
        {
            batch.Clear();

            while (batch.Count < MaxBatch && queue.Reader.TryRead(out var pending))
                batch.Add(pending);

            // Whatever is there goes now, with no waiting for company. The batching comes from the
            // commit itself: everything that arrives while one insert is in flight is picked up by
            // the next, so the batch grows exactly as fast as the load that justifies it.
            //
            // A timed window was tried and is worse in both directions. It taxes a quiet channel the
            // full window per message — senders wait for the commit, so that tax lands on them, and a
            // single channel measured 115 messages a second against 400 without it — while under real
            // load the queue is never empty and the window never elapses anyway.
            await WriteAsync(batch, stoppingToken);
        }
    }

    private async Task WriteAsync(List<Pending> batch, CancellationToken ct)
    {
        var rows = batch.Where(x => x.Message is not null).ToList();

        if (rows.Count > 0)
        {
            try
            {
                await using var db = await context.CreateDbContextAsync(ct);

                await db.Messages.AddRangeAsync(rows.Select(x => x.Message!), ct);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception e)
            {
                // The senders were told the message was accepted, so this is loss, not a failed call.
                logger.LogError(e, "failed to commit a batch of {Count} message(s); they are lost", rows.Count);

                foreach (var pending in batch)
                    pending.Committed.TrySetException(e);

                return;
            }

            // After the commit, so a retry that arrives while the batch is in flight is not told the
            // message exists before it does.
            foreach (var pending in rows)
            {
                try
                {
                    await deduplication.SetDeduplicationAsync(pending.Message!, pending.RandomId, ct);
                }
                catch (Exception e)
                {
                    // A missing dedup key costs a duplicate on retry, not correctness of the write.
                    logger.LogWarning(e, "failed to record the dedup key for message {MessageId}",
                        pending.Message!.MessageId);
                }
            }
        }

        foreach (var pending in batch)
            pending.Committed.TrySetResult();
    }

    public async override Task StopAsync(CancellationToken cancellationToken)
    {
        // Drain rather than drop: the senders have already been told these landed.
        queue.Writer.TryComplete();

        var remaining = new List<Pending>();

        while (queue.Reader.TryRead(out var pending))
            remaining.Add(pending);

        if (remaining.Count > 0)
        {
            logger.LogInformation("draining {Count} queued message(s) before shutdown", remaining.Count);
            await WriteAsync(remaining, CancellationToken.None);
        }

        await base.StopAsync(cancellationToken);
    }
}
