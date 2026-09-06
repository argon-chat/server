namespace Argon.Features.Logic;

using Argon.Features.Clustering;

/// <summary>
/// Every clock and every bound the GDPR export runs on, in one place.
/// </summary>
/// <remarks>
/// <para>These were four <c>static readonly</c> fields and a <c>const</c> at the top of
/// <c>UserDataExportGrain</c>, and like the presence timings they are a system rather than five
/// independent numbers: the archive has to outlive at least one tick of the pump that produced it,
/// the fast first tick has to be no slower than the steady one it precedes, and the batch size
/// decides how much of a busy channel a person actually receives. Written down as constants those
/// relationships were invisible; written down here <see cref="Validate"/> is what enforces them.</para>
///
/// <para><b>The defaults are exactly the constants that shipped</b> — thirty seconds a tick, one
/// second to the first, a thirty-day rate limit, a forty-eight-hour archive, two hundred messages a
/// batch — and nothing in a deployment is expected to change them. They are configuration for the
/// integration suite, which asserts what an export does across a tick boundary, what a second request
/// inside the rate-limit window answers, what the status says once the archive has expired, and what
/// happens to the messages past the end of a batch. Against shipped values the last of those is
/// untestable at all (two hundred and one messages per channel, per case) and the rest cost days;
/// against a one-second tick and a five-message batch they cost seconds and assert the same thing,
/// because every one of them is written as a ratio of these values rather than as a wall-clock
/// number.</para>
/// </remarks>
public sealed class DataExportOptions : IValidatableFeatureOptions
{
    public const string SectionName = "DataExport";

    /// <summary>
    /// The period of the grain timer that drives an export — one step of collection per tick.
    /// </summary>
    /// <remarks>
    /// Deliberately unhurried rather than tuned: an export is a background job on a role that also
    /// runs the deletion and report grains, and every tick is a database round trip plus an object
    /// store <c>PUT</c>. The user is told to come back later and does; nothing is waiting on it.
    /// </remarks>
    public TimeSpan ProcessInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long after a request the first tick lands, ahead of the steady cadence.</summary>
    /// <remarks>
    /// A request answers with a job id and the client immediately reads the status back; a first tick
    /// on the full <see cref="ProcessInterval"/> would leave the screen on "queued" for half a minute
    /// after a button press that did work. Short on purpose, and no shorter than it needs to be,
    /// because the tick it schedules races the state write the request is still finishing.
    /// </remarks>
    public TimeSpan FirstTickDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long after a <em>completed</em> export the next request is refused.
    /// </summary>
    /// <remarks>
    /// Measured from the last completion rather than the last request, so a failed or cancelled
    /// export costs nothing: refusing a person their data because the previous attempt broke is the
    /// one outcome this must never produce. Thirty days is the interval the regulation's "reasonable
    /// intervals" language is usually read as, and it is what the account console renders.
    /// </remarks>
    public TimeSpan RateLimitPeriod { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a finished archive is downloadable: the presigned URL's lifetime, and the age at
    /// which the status flips to <c>Expired</c>.
    /// </summary>
    /// <remarks>
    /// One number for both on purpose — the URL is signed for exactly as long as the grain claims the
    /// archive is available, so a link that reads as live is never one the store has already stopped
    /// honouring. The expiry itself is evaluated lazily, on every read of the status, rather than by a
    /// timer: a grain that has been collected has no timer, and an archive whose window elapsed while
    /// nobody was looking still has to read as expired the moment somebody does.
    /// </remarks>
    public TimeSpan ArchiveTtl { get; set; } = TimeSpan.FromHours(48);

    /// <summary>How many of the caller's messages one channel or conversation contributes.</summary>
    /// <remarks>
    /// A ceiling rather than a page size: the collector takes this many and moves on, so it is
    /// literally how much of a busy channel a person receives. That is a product decision and a
    /// defensible one at two hundred; it is here because it is also the single number that makes the
    /// truncation testable — six messages against a batch of five say everything two hundred and one
    /// against two hundred would.
    /// </remarks>
    public int MessageBatchSize { get; set; } = 200;

    /// <summary>
    /// The relationships between the values, which is the only reason they are one options class.
    /// </summary>
    /// <remarks>
    /// Every rule is a way a deployment could look plausible and hand somebody an archive that is
    /// empty, unreachable, or gone before they could fetch it, so all of them are errors. There is no
    /// upper bound on anything: a long rate limit is a product decision, an archive that expires
    /// before the job that writes it can finish a tick is not.
    /// </remarks>
    public void Validate(IFeatureConfigurationReport report)
    {
        foreach (var (name, value) in new[]
                 {
                     (nameof(ProcessInterval), ProcessInterval),
                     (nameof(FirstTickDelay), FirstTickDelay),
                     (nameof(RateLimitPeriod), RateLimitPeriod),
                     (nameof(ArchiveTtl), ArchiveTtl)
                 })
            report.Require(value > TimeSpan.Zero, name,
                $"is {value}; every export interval is a duration something waits out, and zero or " +
                "negative means the thing it paces either never happens or happens continuously");

        report.Require(MessageBatchSize > 0, nameof(MessageBatchSize),
            $"is {MessageBatchSize}; the archive would carry a channels folder with no messages in " +
            "it, which is an export that silently omits the data it exists to hand over");

        // The first tick is meant to be the fast one. Longer than the steady period it precedes, it
        // is not an optimisation any more, it is a delay in front of every export.
        report.Require(FirstTickDelay <= ProcessInterval, nameof(FirstTickDelay),
            $"is {FirstTickDelay}, above the {ProcessInterval} steady tick; it exists to get the " +
            "first step done before the client's first status poll, so a value above the ordinary " +
            "period only delays every export");

        // The archive is written on one tick and read by a person some time later. A window narrower
        // than the cadence that produced it can lapse between the tick that completed the export and
        // the one that would have told anybody about it.
        report.Require(ArchiveTtl > ProcessInterval, nameof(ArchiveTtl),
            $"is {ArchiveTtl}, at or below the {ProcessInterval} tick that produces the archive; the " +
            "download would be expired by the time the export it belongs to is reported complete");

        // A limit inside a single tick is not a limit: the same request retried on the next poll is
        // already outside it, and the grain would build a second archive for every impatient client.
        report.Require(RateLimitPeriod > ProcessInterval, nameof(RateLimitPeriod),
            $"is {RateLimitPeriod}, at or below the {ProcessInterval} tick; a client polling its own " +
            "status would be outside the window before the export it started had finished, so the " +
            "limit would refuse nothing");
    }
}
