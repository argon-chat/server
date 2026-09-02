namespace Argon.Features.Integrations.Crawler;

/// <summary>
/// Consecutive-failure breaker for one dependency. After <paramref name="failureThreshold"/>
/// unanswered requests in a row it opens for <paramref name="openFor"/>; the first request after
/// that is let through, and one more failure opens it again.
/// </summary>
public sealed class CrawlerCircuit(int failureThreshold, TimeSpan openFor, TimeProvider time)
{
    private readonly object gate = new();
    private int             failures;
    private DateTimeOffset? openUntil;

    public bool IsOpen
    {
        get
        {
            lock (gate)
            {
                if (openUntil is null)
                    return false;
                if (time.GetUtcNow() < openUntil.Value)
                    return true;

                // Half-open: one probe is allowed, and a single failure closes the door again.
                openUntil = null;
                failures  = Math.Max(0, failureThreshold - 1);
                return false;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (gate)
        {
            failures  = 0;
            openUntil = null;
        }
    }

    public void RecordFailure()
    {
        lock (gate)
        {
            if (++failures >= failureThreshold)
                openUntil = time.GetUtcNow() + openFor;
        }
    }
}
