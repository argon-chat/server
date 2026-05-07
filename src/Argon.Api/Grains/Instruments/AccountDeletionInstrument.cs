namespace Argon.Grains.Instruments;

using Argon;
using System.Diagnostics;
using System.Diagnostics.Metrics;

public static class AccountDeletionInstrument
{
    private static readonly Meter Meter = Instruments.Meter;

    public static readonly ActivitySource ActivitySource = new("Argon.AccountDeletion");

    public static readonly Counter<long> DeletionsRequested = Meter.CreateCounter<long>(
        InstrumentNames.DeletionRequested,
        description: "Total account deletion requests");

    public static readonly Counter<long> DeletionsScheduled = Meter.CreateCounter<long>(
        InstrumentNames.DeletionScheduled,
        description: "Total account deletions successfully scheduled");

    public static readonly Counter<long> DeletionsCompleted = Meter.CreateCounter<long>(
        InstrumentNames.DeletionCompleted,
        description: "Total account deletions executed successfully");

    public static readonly Counter<long> DeletionsFailed = Meter.CreateCounter<long>(
        InstrumentNames.DeletionFailed,
        description: "Total account deletions that failed during execution");

    public static readonly Counter<long> DeletionsCancelled = Meter.CreateCounter<long>(
        InstrumentNames.DeletionCancelled,
        description: "Total account deletions cancelled by user");

    public static readonly Counter<long> DeletionsRejected = Meter.CreateCounter<long>(
        InstrumentNames.DeletionRejected,
        description: "Total deletion requests rejected due to preconditions. Tags: reason");

    public static readonly Counter<long> DeletionRemindersSent = Meter.CreateCounter<long>(
        InstrumentNames.DeletionRemindersSent,
        description: "Total deletion reminder emails sent. Tags: days_before");

    public static readonly Histogram<double> DeletionExecutionDuration = Meter.CreateHistogram<double>(
        InstrumentNames.DeletionExecutionDuration,
        unit: "s",
        description: "Duration of account deletion execution phase");
}
