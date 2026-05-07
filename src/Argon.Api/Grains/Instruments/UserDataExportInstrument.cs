namespace Argon.Grains.Instruments;

using Argon;
using System.Diagnostics;
using System.Diagnostics.Metrics;

public static class UserDataExportInstrument
{
    private static readonly Meter Meter = Instruments.Meter;

    public static readonly ActivitySource ActivitySource = new("Argon.Export");

    public static readonly Counter<long> ExportsRequested = Meter.CreateCounter<long>(
        InstrumentNames.ExportRequested,
        description: "Total data export requests");

    public static readonly Counter<long> ExportsStarted = Meter.CreateCounter<long>(
        InstrumentNames.ExportStarted,
        description: "Total data exports that started processing");

    public static readonly Counter<long> ExportsCompleted = Meter.CreateCounter<long>(
        InstrumentNames.ExportCompleted,
        description: "Total data exports completed successfully");

    public static readonly Counter<long> ExportsFailed = Meter.CreateCounter<long>(
        InstrumentNames.ExportFailed,
        description: "Total data exports that failed. Tags: reason");

    public static readonly Counter<long> ExportsCancelled = Meter.CreateCounter<long>(
        InstrumentNames.ExportCancelled,
        description: "Total data exports cancelled by user");

    public static readonly Counter<long> ExportsRateLimited = Meter.CreateCounter<long>(
        InstrumentNames.ExportRateLimited,
        description: "Total export requests rejected due to rate limiting");

    public static readonly Counter<long> ExportTicksProcessed = Meter.CreateCounter<long>(
        InstrumentNames.ExportTicksProcessed,
        description: "Total processing ticks executed. Tags: phase");

    public static readonly Histogram<double> ExportDuration = Meter.CreateHistogram<double>(
        InstrumentNames.ExportDuration,
        unit: "s",
        description: "Total duration of export from start to completion");

    public static readonly Histogram<double> ExportArchiveSizeBytes = Meter.CreateHistogram<double>(
        InstrumentNames.ExportArchiveSizeBytes,
        unit: "By",
        description: "Size of final export archive");

    public static readonly Histogram<double> ExportTickDuration = Meter.CreateHistogram<double>(
        InstrumentNames.ExportTickDuration,
        unit: "ms",
        description: "Duration of individual processing ticks. Tags: phase");
}
