namespace Argon.Features.k8s;

/// <summary>
/// A client role's answer to "should Kubernetes still be sending me connections".
/// </summary>
/// <remarks>
/// <para>A silo answers that from its drain state. A client role had nothing to answer it with, so
/// the only signal it could give Kubernetes was the process disappearing — and a pod leaves a
/// Service after it stops, not before, so every websocket it held was severed while new connections
/// were still being routed to it. This flag is what lets the pod leave the Service first.</para>
///
/// <para>One direction, and no way back, deliberately. The only thing that sets it is the pre-stop
/// hook, and the pre-stop hook stops the process a fixed wait later whatever happens in between; a
/// flag that could be cleared would be one that lies about what is coming. That is the difference
/// from a silo drain, which is a state a silo can sit in and be recalled from — this one is a
/// countdown.</para>
/// </remarks>
public sealed class ClientStopSignal(IHostApplicationLifetime lifetime)
{
    private long requestedAtTicks;

    /// <summary>When the stop was asked for, or null while nothing has asked.</summary>
    public DateTimeOffset? RequestedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref requestedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Whether this process should be taken out of the Service.
    /// </summary>
    /// <remarks>
    /// True for stops nobody asked this class about as well — a bare SIGTERM, a hosted service that
    /// threw, <c>StopApplication</c> from anywhere else. Readiness has to be false for all of them,
    /// and only the host knows about the rest, so the host is consulted rather than mirrored.
    /// </remarks>
    public bool IsStopping
        => RequestedAt is not null || lifetime.ApplicationStopping.IsCancellationRequested;

    /// <summary>Marks the process not-ready. False if something already had.</summary>
    public bool RequestStop()
        => Interlocked.CompareExchange(ref requestedAtTicks, DateTimeOffset.UtcNow.UtcTicks, 0) == 0;
}
