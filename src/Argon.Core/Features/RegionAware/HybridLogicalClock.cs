namespace Argon.Features.RegionAware;

public sealed class HybridLogicalClock(string nodeId)
{
    private readonly string nodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
    private readonly Lock   _lock   = new();

    private long lastPhysicalMillis = CurrentPhysicalMillis();
    private int  lastLogical        = 0;

    /// <summary>
    /// The last timestamp generated (it can be default if there have been no events yet).
    /// </summary>
    public HybridTimestamp LastTimestamp
    {
        get
        {
            lock (_lock)
            {
                return new HybridTimestamp(lastPhysicalMillis, lastLogical, nodeId);
            }
        }
    }

    /// <summary>
    /// Generate a timestamp for a local event (write, commit, etc.).
    /// </summary>
    public HybridTimestamp NextLocal()
    {
        lock (_lock)
        {
            var now = CurrentPhysicalMillis();

            if (now > lastPhysicalMillis)
            {
                // time has gone ahead — we reset the logical counter
                lastPhysicalMillis = now;
                lastLogical        = 0;
            }
            else
            {
                // time is "standing" or has rolled back (clock skew) — we increase the logical
                lastLogical++;
            }

            return new HybridTimestamp(lastPhysicalMillis, lastLogical, nodeId);
        }
    }

    /// <summary>
    /// Update the clock when receiving a deleted timestamp.
    /// Returns a NEW local timestamp that should be associated with this event.
    /// </summary>
    public HybridTimestamp OnReceive(HybridTimestamp remote)
    {
        lock (_lock)
        {
            var now = CurrentPhysicalMillis();

            // candidatePhysical = max(now, lastPhysical, remotePhysical)
            var candidatePhysical = Math.Max(now, Math.Max(lastPhysicalMillis, remote.PhysicalMillis));

            var isFromLocal  = candidatePhysical == lastPhysicalMillis;
            var isFromRemote = candidatePhysical == remote.PhysicalMillis;
            var isFromNow    = candidatePhysical == now;


            // Classic HLC rule:
            // if all three match -> logical = max(local, remote) + 1
            // if local time wins -> localLogical + 1
            // if remote wins -> remoteLogical + 1
            // if "now" wins (both less) -> logical = 0
            var newLogical = isFromLocal switch
            {
                true when isFromRemote && isFromNow => Math.Max(lastLogical, remote.LogicalCounter) + 1,
                true when isFromRemote              => Math.Max(lastLogical, remote.LogicalCounter) + 1,
                true                                => lastLogical + 1,
                _                                   => isFromRemote ? remote.LogicalCounter + 1 : 0
            };

            lastPhysicalMillis = candidatePhysical;
            lastLogical        = newLogical;

            return new HybridTimestamp(lastPhysicalMillis, lastLogical, nodeId);
        }
    }

    private static long CurrentPhysicalMillis()
        => TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds();
}