namespace Argon.Features.Storage;

using Argon.Features.Clustering;

/// <summary>Shared objects under files: when a stored object is read back for hashing, and how long a merged-away object is kept.</summary>
public sealed class DedupOptions : IValidatableFeatureOptions
{
    public const string SectionName = "Dedup";

    /// <summary>Off: every upload keeps its own object and nothing is read back. Links still work.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>An object larger than this is never read back; it converges only when ETag, size and the client's claim agree.</summary>
    public long MaxVerifyBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>How much the verifier reads from the store per pass.</summary>
    public long VerifyBytesPerMinute { get; set; } = 512L * 1024 * 1024;

    public TimeSpan VerifyInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a merged-away object stays. Longer than the redirect cache keeps a file's key
    /// (<c>CdnRedirectFeature</c>, one day), so a cached redirect to the old key keeps working.
    /// </summary>
    public TimeSpan PhysicalDeleteDelay { get; set; } = TimeSpan.FromHours(36);

    /// <summary>Once per process start, queue every existing object that has an unverified twin. For the release that introduces blobs.</summary>
    public bool Backfill { get; set; }

    public void Validate(IFeatureConfigurationReport report)
    {
        report.RequireRange(VerifyInterval, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), nameof(VerifyInterval));
        report.Require(MaxVerifyBytes >= 0, nameof(MaxVerifyBytes), "must not be negative");
        report.Require(VerifyBytesPerMinute > 0, nameof(VerifyBytesPerMinute), "must be positive");
        report.Prefer(PhysicalDeleteDelay >= TimeSpan.FromHours(25), nameof(PhysicalDeleteDelay),
            "is shorter than the redirect cache keeps a file's key, so a cached redirect can outlive the object it points at");
    }
}
