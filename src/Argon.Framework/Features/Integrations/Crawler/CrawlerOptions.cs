namespace Argon.Features.Integrations.Crawler;

using Argon.Features.Clustering;

/// <summary>
/// The link-preview crawler: argon-crawler, a separate service answering request/reply over NATS.
/// </summary>
public sealed class CrawlerOptions : IValidatableFeatureOptions
{
    public const string SectionName = "Crawler";

    /// <summary>
    /// Off means every lookup answers "unavailable" at once and a message loses its preview stub
    /// without waiting on anything. For a deployment that does not run the crawler.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// NATS subject prefix; the crawler listens on <c>&lt;prefix&gt;.crawl</c>, <c>.invalidate</c>
    /// and <c>.health</c>.
    /// </summary>
    public string SubjectPrefix { get; set; } = "argon.crawler";

    /// <summary>
    /// How long a lookup may take when no message is waiting on it: the composer's preview and the
    /// deferred resolution after a send. Bounded by what the crawler itself allows a page — robots,
    /// fetch and image add up to about ten seconds in its defaults — so more buys nothing.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a send waits for the preview before the message goes out without it. The composer
    /// normally asked for the same page a moment earlier, so this is a cache hit measured in
    /// milliseconds; a miss keeps crawling and reaches clients through <c>MessageUpdated</c>.
    /// </summary>
    public TimeSpan SendBudget { get; set; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Whether a preview may point clients at the page's own image when the crawler holds no
    /// re-hosted copy. Off by default: every reader would then fetch from the linked site and show it
    /// their address. On is for development, where the crawler runs without S3.
    /// </summary>
    public bool AllowExternalImages { get; set; } = false;

    /// <summary>Composer lookups one user may make per minute, counted per node.</summary>
    public int PreviewRequestsPerMinute { get; set; } = 60;

    /// <summary>
    /// After this many unanswered requests in a row the crawler is taken to be down for
    /// <see cref="CircuitOpenFor"/>, and lookups fail at once instead of each paying a timeout.
    /// </summary>
    public int CircuitFailureThreshold { get; set; } = 3;

    public TimeSpan CircuitOpenFor { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate(IFeatureConfigurationReport report)
    {
        report.Require(!string.IsNullOrWhiteSpace(SubjectPrefix), nameof(SubjectPrefix), "cannot be empty");
        report.Require(Timeout > TimeSpan.Zero, nameof(Timeout), "must be positive");
        report.Require(SendBudget > TimeSpan.Zero, nameof(SendBudget), "must be positive");
        report.Require(SendBudget <= Timeout, nameof(SendBudget),
            $"cannot exceed {nameof(Timeout)}; a send waits for less than a lookup, never more");
        report.RequireRange(PreviewRequestsPerMinute, 1, 10_000, nameof(PreviewRequestsPerMinute));
        report.RequireRange(CircuitFailureThreshold, 1, 100, nameof(CircuitFailureThreshold));
        report.Require(CircuitOpenFor > TimeSpan.Zero, nameof(CircuitOpenFor), "must be positive");
    }
}
