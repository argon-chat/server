namespace Argon.Core.Features.Transport;

using Argon.Features.Auth;
using Argon.Features.Logic;
using Argon.Services;
using Microsoft.Extensions.Caching.Hybrid;
using NATS.Client.Core;
using NATS.Net;

/// <summary>
/// The bus subject a sign-out is announced on.
/// </summary>
/// <remarks>
/// Core pub/sub rather than JetStream, deliberately. A stream would give delivery guarantees this
/// message has no use for: it is worth acting on for the second or two while the socket it names is
/// still open, and a copy replayed to a node that starts an hour later would name connections that
/// have not existed since. Fan-out to every subscriber is the property that matters — a user's
/// devices are spread across nodes and none of them knows which — and that is what a plain subject
/// gives, with no consumer to create and nothing to acknowledge.
/// </remarks>
public static class SessionRevocationSubjects
{
    public const string Revoked = "argon.session.revoked";
}

/// <summary>One session, ended, in the terms the nodes holding its sockets can act on.</summary>
/// <param name="SessionId">
/// The presence sid the sign-out was pressed on, or null where the caller had none — a browser tab
/// signing out from a request that carried no session header, say.
/// </param>
/// <param name="CredentialSessionIds">
/// Every server-minted credential id tombstoned with it. The half that reaches a device which came
/// back under a freshly generated <c>scid</c>.
/// </param>
public sealed record SessionRevocationSignal(
    Guid                UserId,
    Guid?               SessionId,
    IReadOnlyList<Guid> CredentialSessionIds);

/// <summary>Announces an ended session to every node that might be holding one of its sockets.</summary>
public interface ISessionRevocationBroadcaster
{
    /// <summary>
    /// Publishes the sign-out. Never throws: the caller has already committed the tombstone, which is
    /// the truth, and this is only its delivery.
    /// </summary>
    ValueTask PublishAsync(
        Guid                      userId,
        Guid?                     sessionId,
        IReadOnlyCollection<Guid> credentialSessionIds,
        CancellationToken         ct = default);
}

/// <inheritdoc cref="ISessionRevocationBroadcaster"/>
/// <remarks>
/// <para>On the connection <c>BotEventPublisher</c> already holds, because there is exactly one NATS
/// client per process and a second would buy a second TCP connection and a second set of reconnect
/// semantics for one small message.</para>
///
/// <para><b>Fail-soft is the contract, not an implementation detail.</b> Everything that calls this
/// has already written the tombstone, and the tombstone is what actually revokes: the Ion gate, the
/// hub gate and the session grain all read it, and the sweep re-reads it on a timer. So a bus that
/// is down costs a signed-out device the difference between "closed now" and "closed within a sweep",
/// and letting the failure escape would instead cost the user a sign-out that reported failure while
/// having fully succeeded.</para>
/// </remarks>
public sealed class NatsSessionRevocationBroadcaster(
    INatsClient                               nats,
    ILogger<NatsSessionRevocationBroadcaster> logger) : ISessionRevocationBroadcaster
{
    public async ValueTask PublishAsync(
        Guid                      userId,
        Guid?                     sessionId,
        IReadOnlyCollection<Guid> credentialSessionIds,
        CancellationToken         ct = default)
    {
        try
        {
            var signal = new SessionRevocationSignal(userId, sessionId, credentialSessionIds.ToArray());

            // Published as a string rather than as a typed payload: the serializer registry the client
            // is built with handles strings natively, so the wire format is one this file decides and
            // no NATS-side configuration can change underneath it.
            await nats.PublishAsync(
                SessionRevocationSubjects.Revoked, JsonConvert.SerializeObject(signal), cancellationToken: ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Could not announce the sign-out of session {SessionId} for user {UserId}; the sockets it holds " +
                "will be closed by the connection sweep instead", sessionId, userId);
        }
    }
}

/// <summary>
/// Listens for sign-outs raised anywhere in the cluster and closes the sockets they name here.
/// </summary>
/// <remarks>
/// <para>Layer two of the three in <see cref="AppHub"/>: it is what makes a sign-out immediate
/// instead of eventual. A user's devices are spread over whichever nodes accepted their connections,
/// and the grain that ends a session runs on a silo that holds none of them, so the only thing that
/// can reach the socket is a message every node listens to.</para>
///
/// <para>Runs only where the hub is mapped — see <c>SignalRHubExtensions.AddAppHubEndpoint</c> — and
/// resubscribes for as long as the process lives. A dropped subscription is the one failure that
/// would be silent and permanent: nothing else on this path ever fails, so the symptom would be
/// sign-outs that quietly take a sweep instead of a moment, indefinitely.</para>
/// </remarks>
public sealed class SessionRevocationSubscriber(
    INatsClient                          nats,
    HubConnectionRegistry                registry,
    ILogger<SessionRevocationSubscriber> logger) : BackgroundService
{
    /// <summary>How long to wait before resubscribing after the subscription faulted.</summary>
    /// <remarks>
    /// Short, because the window it opens is one in which sign-outs are only as fast as the sweep,
    /// and long enough that a bus that is genuinely down is not being hammered by every node at once.
    /// </remarks>
    private static readonly TimeSpan ResubscribeDelay = TimeSpan.FromSeconds(5);

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var message in nats
                                  .SubscribeAsync<string>(SessionRevocationSubjects.Revoked, cancellationToken: stoppingToken)
                                  .ConfigureAwait(false))
                {
                    if (message.Data is { Length: > 0 } payload)
                        await ApplyAsync(payload);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "The session-revocation subscription faulted; resubscribing in {Delay}",
                    ResubscribeDelay);
            }

            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                await Task.Delay(ResubscribeDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Closes whatever this node is holding for the session the message names.</summary>
    /// <remarks>
    /// Guarded per message: a payload this build cannot read — an older node, a message from
    /// somewhere else on the subject — must not take the subscription down with it, because that
    /// would cost every <em>other</em> sign-out its delivery.
    /// </remarks>
    private async Task ApplyAsync(string payload)
    {
        try
        {
            if (JsonConvert.DeserializeObject<SessionRevocationSignal>(payload) is not { } signal)
                return;

            var closed = 0;

            if (signal.SessionId is { } sessionId)
                closed += await registry.AbortSessionAsync(signal.UserId, sessionId);

            foreach (var credentialSessionId in signal.CredentialSessionIds ?? [])
                closed += await registry.AbortCredentialAsync(signal.UserId, credentialSessionId);

            if (closed > 0)
                logger.LogInformation(
                    "Closed {Count} hub connection(s) of session {SessionId} for user {UserId} on a sign-out signal",
                    closed, signal.SessionId, signal.UserId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not act on a session-revocation message");
        }
    }
}

/// <summary>
/// Re-reads the revocation state of every socket this node holds, on a timer.
/// </summary>
/// <remarks>
/// <para>Layer three of the three in <see cref="AppHub"/>, and the only one that depends on nothing
/// but the store the revocation itself lives in. The gate on the calls needs the client to speak and
/// the signal needs the bus to deliver; a silent client on a node that missed one message would
/// otherwise keep its feed until it disconnected of its own accord, which for a client that has been
/// deliberately silenced is never.</para>
///
/// <para><b>The period is derived rather than chosen.</b> It is a third of
/// <see cref="PresenceTimingOptions.StaleConnectionAfter"/> — the interval after which the session
/// stops counting a connection nothing has been heard from — capped at fifteen seconds, which is the
/// cadence the shipped client heartbeats on and therefore the resolution the call gate already has.
/// Sweeping faster than that would only re-ask a question the heartbeats are answering; sweeping
/// slower would leave the floor above the ceiling it is meant to be under. A host that compresses its
/// presence clocks gets a proportionally faster sweep for free, which is what makes this observable
/// in a test at all.</para>
///
/// <para><b>One read per user, and the legacy key shape is deliberately not among them.</b> The set
/// and the floor are read once per distinct user per sweep and every connection of that user is
/// decided from the pair, so a node holding thousands of sockets for hundreds of users costs
/// hundreds of cached lookups rather than thousands of round trips. The pre-set tombstones
/// (<c>SessionRevocation.LegacyRevokedKey</c>) are skipped because they are a compatibility shim that
/// nothing writes any more and the call gate reads them on every call regardless — paying for them
/// here would double the sweep's cost to shorten a window that only exists for revocations written
/// before the set did.</para>
///
/// <para>Fails open, per user: a store error skips that user's connections this round rather than
/// closing them, for the reason the call gate fails open on an established socket — a Redis incident
/// must not sign the whole instance out.</para>
/// </remarks>
public sealed class HubConnectionSweeper(
    HubConnectionRegistry           registry,
    IArgonCacheDatabase             store,
    HybridCache                     cache,
    IOptions<PresenceTimingOptions> timings,
    ILogger<HubConnectionSweeper>   logger) : BackgroundService
{
    /// <summary>The ceiling on the sweep period: the cadence the shipped client heartbeats on.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The floor on the sweep period.
    /// </summary>
    /// <remarks>
    /// <see cref="PresenceTimingOptions.Validate"/> already refuses a stale-connection interval close
    /// to the refresh tick, so this cannot be reached by any configuration a host would start on. It
    /// is here so that a value which somehow got past it cannot turn the sweep into a spin.
    /// </remarks>
    private static readonly TimeSpan Floor = TimeSpan.FromSeconds(1);

    /// <inheritdoc cref="HubConnectionSweeper"/>
    public static TimeSpan PeriodFor(PresenceTimingOptions options)
    {
        var third = options.StaleConnectionAfter / 3;

        return third > Ceiling ? Ceiling : third < Floor ? Floor : third;
    }

    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = PeriodFor(timings.Value);

        logger.LogDebug("Hub connection sweep runs every {Period}", period);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(period, stoppingToken);
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "The hub connection sweep failed; it will run again in {Period}", period);
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var live = registry.Snapshot();

        if (live.Count == 0)
            return;

        var doomed = new List<HubConnectionEntry>();

        foreach (var perUser in live.GroupBy(x => x.UserId))
        {
            SessionRevocation.RevocationState state;

            try
            {
                state = await SessionRevocation.ReadStateAsync(store, cache, perUser.Key, ct);
            }
            catch (Exception e)
            {
                logger.LogWarning(e,
                    "Could not read the revocation state of user {UserId} during the connection sweep; their " +
                    "connections are left alone this round", perUser.Key);

                continue;
            }

            // Nothing has ever been revoked for this user and no floor was ever written — the common
            // case by a very long way, and one that costs nothing beyond the read above.
            if (state.Revoked.Length == 0 && state.Floor is null)
                continue;

            doomed.AddRange(perUser.Where(entry => state.Ends(entry.Identities, entry.TicketIssuedAt)));
        }

        if (doomed.Count == 0)
            return;

        var closed = await registry.Abort(doomed, "the sweep found it signed out");

        logger.LogInformation(
            "The hub connection sweep closed {Closed} of {Found} signed-out connection(s) out of {Live} live",
            closed, doomed.Count, live.Count);
    }
}
