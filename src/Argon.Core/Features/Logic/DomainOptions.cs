namespace Argon.Features.Logic;

using Argon.Features.Clustering;

/// <summary>Distributed id generation.</summary>
public sealed class SnowflakeOptions : IValidatableFeatureOptions
{
    /// <summary>
    /// Identifies this deployment's id space. Two processes generating ids with the same datacenter
    /// id can collide, so a multi-region deployment has to give each region its own.
    /// </summary>
    [Range(0, 31)]
    public int DataCenterId { get; set; } = 1;

    public bool UseConsoleLog { get; set; } = true;

    public void Validate(IFeatureConfigurationReport report)
        => report.Prefer(!report.SectionExists || DataCenterId != 1 || !IsMultiRegion(), nameof(DataCenterId),
            "is the default while ARGON_REGION_DC names a region; ids from two regions can collide");

    private static bool IsMultiRegion()
        => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARGON_REGION_DC"));
}

/// <summary>How often orphaned blobs are swept, and how long a fresh one is left alone.</summary>
public sealed class FileGcOptions : IValidatableFeatureOptions
{
    public TimeSpan BlobSweepInterval   { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan OrphanSweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a blob with no owner is spared. This is the window an upload has to be claimed in;
    /// shorter than the slowest upload-then-attach flow and the collector eats live data.
    /// </summary>
    public TimeSpan OrphanGracePeriod { get; set; } = TimeSpan.FromHours(1);

    public void Validate(IFeatureConfigurationReport report)
    {
        report.RequireRange(BlobSweepInterval, TimeSpan.FromSeconds(10), TimeSpan.FromDays(1), nameof(BlobSweepInterval));
        report.RequireRange(OrphanSweepInterval, TimeSpan.FromSeconds(10), TimeSpan.FromDays(1), nameof(OrphanSweepInterval));

        report.Prefer(OrphanGracePeriod >= TimeSpan.FromMinutes(10), nameof(OrphanGracePeriod),
            "is short enough that a slow upload could be collected before it is attached");
    }
}
