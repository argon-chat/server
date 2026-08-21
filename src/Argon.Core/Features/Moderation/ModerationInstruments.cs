namespace Argon.Features.Moderation;

using System.Diagnostics;
using System.Diagnostics.Metrics;

public static class ModerationInstruments
{
    private static readonly Meter Meter = Instruments.Meter;

    public static readonly ActivitySource ActivitySource = new("Argon.Moderation");

    // ── Evaluation counters ─────────────────────────────────────────────

    public static readonly Counter<long> EvaluationsTotal = Meter.CreateCounter<long>(
        InstrumentNames.ModerationEvaluationsTotal,
        description: "Total content moderation evaluations. Tags: purpose, action");

    public static readonly Counter<long> RejectionsTotal = Meter.CreateCounter<long>(
        InstrumentNames.ModerationRejectionsTotal,
        description: "Total content moderation rejections. Tags: purpose");

    public static readonly Counter<long> EvaluationsSkipped = Meter.CreateCounter<long>(
        InstrumentNames.ModerationEvaluationsSkipped,
        description: "Total evaluations skipped (model unavailable). Tags: purpose");

    public static readonly Counter<long> ViolationsRecorded = Meter.CreateCounter<long>(
        InstrumentNames.ModerationViolationsRecorded,
        description: "Total violation records persisted. Tags: purpose");

    // ── Timing histograms ───────────────────────────────────────────────

    public static readonly Histogram<double> EvaluationDurationMs = Meter.CreateHistogram<double>(
        InstrumentNames.ModerationEvaluationDuration,
        unit: "ms",
        description: "Total evaluation duration (download + inference). Tags: purpose, stages_used");

    public static readonly Histogram<double> S3DownloadDurationMs = Meter.CreateHistogram<double>(
        InstrumentNames.ModerationS3DownloadDuration,
        unit: "ms",
        description: "S3 image download duration. Tags: purpose");

    public static readonly Histogram<double> InferenceDurationMs = Meter.CreateHistogram<double>(
        InstrumentNames.ModerationInferenceDuration,
        unit: "ms",
        description: "ONNX inference duration. Tags: purpose, stage");

    // ── Concurrency gauge ───────────────────────────────────────────────

    public static readonly UpDownCounter<long> ActiveInferences = Meter.CreateUpDownCounter<long>(
        InstrumentNames.ModerationActiveInferences,
        description: "Current number of concurrent moderation inferences");
}
