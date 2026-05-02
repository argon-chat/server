namespace Argon.Features.Storage;

using System.Diagnostics;
using System.Diagnostics.Metrics;

public static class StorageInstruments
{
    private static readonly Meter Meter = Instruments.Meter;

    public static readonly ActivitySource ActivitySource = new("Argon.Storage");

    // ── Uploads ─────────────────────────────────────────────────────────

    public static readonly Counter<long> UploadsRequested = Meter.CreateCounter<long>(
        InstrumentNames.StorageUploadsRequested,
        description: "Total upload requests initiated. Tags: purpose");

    public static readonly Counter<long> UploadsFinalized = Meter.CreateCounter<long>(
        InstrumentNames.StorageUploadsFinalized,
        description: "Total uploads successfully finalized. Tags: purpose");

    public static readonly Counter<long> UploadsFailed = Meter.CreateCounter<long>(
        InstrumentNames.StorageUploadsFailed,
        description: "Total upload failures. Tags: purpose, reason");

    public static readonly Histogram<double> UploadSizeBytes = Meter.CreateHistogram<double>(
        InstrumentNames.StorageUploadSizeBytes,
        unit: "By",
        description: "Distribution of uploaded file sizes. Tags: purpose");

    public static readonly Histogram<double> UploadFinalizeDuration = Meter.CreateHistogram<double>(
        InstrumentNames.StorageUploadFinalizeDuration,
        unit: "ms",
        description: "Duration of upload finalization (HEAD + DB). Tags: purpose");

    // ── Downloads / URL generation ──────────────────────────────────────

    public static readonly Counter<long> PresignedGetGenerated = Meter.CreateCounter<long>(
        InstrumentNames.StoragePresignedGetGenerated,
        description: "Total presigned GET URLs generated. Tags: purpose");

    public static readonly Counter<long> PublicUrlsServed = Meter.CreateCounter<long>(
        InstrumentNames.StoragePublicUrlsServed,
        description: "Total public URL lookups. Tags: purpose");

    // ── Reference Counting ──────────────────────────────────────────────

    public static readonly Counter<long> RefIncrements = Meter.CreateCounter<long>(
        InstrumentNames.StorageRefIncrements,
        description: "Total reference count increments");

    public static readonly Counter<long> RefDecrements = Meter.CreateCounter<long>(
        InstrumentNames.StorageRefDecrements,
        description: "Total reference count decrements");

    // ── Garbage Collection ──────────────────────────────────────────────

    public static readonly Counter<long> GcBlobsSwept = Meter.CreateCounter<long>(
        InstrumentNames.StorageGcBlobsSwept,
        description: "Total expired blobs cleaned up by GC");

    public static readonly Counter<long> GcOrphansSwept = Meter.CreateCounter<long>(
        InstrumentNames.StorageGcOrphansSwept,
        description: "Total orphan files (ref≤0) cleaned up by GC");

    public static readonly Counter<long> GcErrors = Meter.CreateCounter<long>(
        InstrumentNames.StorageGcErrors,
        description: "Total GC sweep errors. Tags: sweep_type");

    public static readonly Histogram<double> GcSweepDuration = Meter.CreateHistogram<double>(
        InstrumentNames.StorageGcSweepDuration,
        unit: "ms",
        description: "Duration of GC sweep iterations. Tags: sweep_type");

    // ── S3 Operations ───────────────────────────────────────────────────

    public static readonly Counter<long> S3Operations = Meter.CreateCounter<long>(
        InstrumentNames.StorageS3Operations,
        description: "Total S3 operations. Tags: operation (head, delete, put), status (success, failed)");

    public static readonly Histogram<double> S3OperationDuration = Meter.CreateHistogram<double>(
        InstrumentNames.StorageS3OperationDuration,
        unit: "ms",
        description: "Duration of S3 operations. Tags: operation");

    // ── Active state ────────────────────────────────────────────────────

    public static readonly UpDownCounter<long> ActiveBlobs = Meter.CreateUpDownCounter<long>(
        InstrumentNames.StorageActiveBlobs,
        description: "Current number of active (non-expired) upload blobs");

    public static readonly Counter<long> TotalStoredBytes = Meter.CreateCounter<long>(
        InstrumentNames.StorageTotalStoredBytes,
        unit: "By",
        description: "Cumulative bytes stored. Tags: purpose");
}
