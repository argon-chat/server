namespace Argon.Features.Clustering.Regions;

using NATS.Client.Core;
using System.Runtime.CompilerServices;

/// <summary>How a declaration leaves this process, and how everyone else's arrive.</summary>
/// <remarks>
/// An interface because the announcement channel is the replaceable half of this design and the
/// merge rule is not. A deployment with no bus, and every test, uses
/// <see cref="NullRegionIntentChannel"/> and still gets an honest local answer — silence means
/// <see cref="RegionIntent.Active"/>, which is exactly what a region that has declared nothing is.
/// </remarks>
public interface IRegionIntentChannel
{
    ValueTask PublishAsync(RegionIntentAnnouncement announcement, CancellationToken ct = default);

    /// <summary>Every region's declarations, including this region's own, until cancelled.</summary>
    IAsyncEnumerable<RegionIntentAnnouncement> ListenAsync(CancellationToken ct = default);
}

/// <summary>
/// The announcement channel over NATS: a subject per region, everyone subscribed to all of them.
/// </summary>
/// <remarks>
/// <para>A subject per region rather than one shared subject, so that a region only ever speaks
/// under its own name and a subscriber can narrow to the regions it cares about without filtering
/// payloads. The published subject is derived from the region name and the payload carries the name
/// again; the replica keys on the payload, so a name that has to be mangled to fit a subject token
/// still lands under the name everything else uses.</para>
///
/// <para>Core NATS, not JetStream: an announcement is a statement about right now, and a replayed
/// month-old "draining" would be worse than no announcement at all. The cost is that a subscriber
/// which was not listening at the time hears nothing, which is why declarations are repeated — see
/// <see cref="RegionIntentAnnouncer"/>.</para>
///
/// <para><b>How far an announcement actually travels is the deployment's answer, not this class's.</b>
/// A subject reaches whoever is connected to the same NATS, and today every role reads one
/// <c>ConnectionStrings:nats</c> — one NATS per region — so what this delivers is convergence
/// <em>within</em> a region: whichever pod the operator declared on, the region's other pods adopt the
/// same answer and repeat it, and "is this region draining" stops depending on which pod is asked.
/// Reaching the other regions needs those NATS servers gatewayed into a supercluster, which is a
/// server-side link and requires no change here — a gateway propagates the subject's interest, and the
/// subscription is already the all-regions wildcard. Until that link exists, a peer's status is what
/// this process can observe of it, which is what <see cref="RegionAvailability.Merge"/> falls back to
/// when no announcement has been heard.</para>
/// </remarks>
public sealed class NatsRegionIntentChannel(INatsClient nats, ILogger<NatsRegionIntentChannel> logger)
    : IRegionIntentChannel
{
    private const string SubjectRoot   = "argon.regions";
    private const string SubjectLeaf   = "intent";

    /// <summary>Every region's subject.</summary>
    public const string AllSubject = $"{SubjectRoot}.*.{SubjectLeaf}";

    /// <summary>
    /// The subject one region declares on.
    /// </summary>
    /// <remarks>
    /// Lower-cased because region names are compared case-insensitively everywhere else and NATS
    /// subjects are not, so "RU-A" and "ru-a" have to reach the same subscribers. Anything that is
    /// not a subject token character becomes an underscore — a dot in a region name would otherwise
    /// silently split the subject into an extra level and stop matching the wildcard.
    /// </remarks>
    public static string SubjectFor(string region)
        => $"{SubjectRoot}.{Token(region)}.{SubjectLeaf}";

    private static string Token(string region)
    {
        var token = new StringBuilder(region.Length);

        foreach (var c in region)
            token.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '_');

        return token.Length == 0 ? "_" : token.ToString();
    }

    public async ValueTask PublishAsync(RegionIntentAnnouncement announcement, CancellationToken ct = default)
        => await nats.PublishAsync(SubjectFor(announcement.Region), announcement, cancellationToken: ct);

    public async IAsyncEnumerable<RegionIntentAnnouncement> ListenAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var message in nats.SubscribeAsync<RegionIntentAnnouncement>(AllSubject, cancellationToken: ct))
        {
            if (message.Data is not { } announcement || string.IsNullOrWhiteSpace(announcement.Region))
            {
                logger.LogWarning("Unreadable region announcement on '{Subject}'", message.Subject);
                continue;
            }

            yield return announcement;
        }
    }
}

/// <summary>A channel with nothing on the other end.</summary>
/// <remarks>
/// Not a degraded mode: a process with no way to hear announcements answers
/// <see cref="RegionIntent.Active"/> for every region it has not been told about, and the merge rule
/// means that answer cannot make anything look better than this process can actually reach.
/// </remarks>
public sealed class NullRegionIntentChannel : IRegionIntentChannel
{
    public static readonly NullRegionIntentChannel Instance = new();

    public ValueTask PublishAsync(RegionIntentAnnouncement announcement, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public async IAsyncEnumerable<RegionIntentAnnouncement> ListenAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Waits rather than ending, so a caller pumping this does not spin on an enumerable that
        // completes immediately.
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);

        yield break;
    }
}

/// <summary>
/// Keeps this process's replica of what every region says, and keeps repeating what this one says.
/// </summary>
/// <remarks>
/// <para>The repeat is not a heartbeat about health — nothing here ever publishes reachability. It
/// exists because an announcement has no retention: a region that began draining before a peer's
/// pods started would look <see cref="RegionIntent.Active"/> to that peer for as long as it stayed
/// quiet. Repeating converges a late listener within one period, in both directions, which a
/// publish-only-on-transition channel cannot do.</para>
///
/// <para>Only a process that has actually been told a declaration repeats one. A process that has
/// heard nothing stays silent, so silence keeps meaning <see cref="RegionIntent.Active"/> and a pod
/// that restarted in the middle of a drain cannot announce the region back into service.</para>
/// </remarks>
public sealed class RegionIntentAnnouncer(
    IRegionIntents intents,
    IRegionIntentChannel channel,
    IOptions<ArgonRegionOptions> options,
    ILogger<RegionIntentAnnouncer> logger) : BackgroundService
{
    /// <summary>How long to wait before resubscribing after the listener falls over.</summary>
    private static readonly TimeSpan ListenRetry = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        => await Task.WhenAll(ListenAsync(stoppingToken), RepeatAsync(stoppingToken));

    /// <summary>
    /// Subscribes, and keeps subscribing.
    /// </summary>
    /// <remarks>
    /// Nothing thrown in here is allowed out. A background service that throws takes the host down
    /// by default, which would mean a bus hiccup killing every process in the region — an
    /// unreachable announcement channel has to degrade to "hears nothing", and hearing nothing is a
    /// state the merge rule already handles safely.
    /// </remarks>
    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var announcement in channel.ListenAsync(ct))
                {
                    if (intents.Record(announcement))
                        logger.LogInformation("Region '{Region}' is {Intent} (declared {DeclaredAt:O})",
                            announcement.Region, announcement.Intent, announcement.DeclaredAt);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Region announcements stopped arriving; resubscribing in {Delay}", ListenRetry);
            }

            try
            {
                await Task.Delay(ListenRetry, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RepeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(options.Value.IntentHeartbeat);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (intents.Declaration is not { } declaration)
                    continue;

                try
                {
                    await channel.PublishAsync(declaration, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Could not repeat region '{Region}' intent {Intent}",
                        declaration.Region, declaration.Intent);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }
}
