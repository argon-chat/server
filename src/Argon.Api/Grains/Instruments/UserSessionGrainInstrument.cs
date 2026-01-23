namespace Argon.Grains.Instruments;

using Argon;
using System.Diagnostics.Metrics;

public static class UserSessionGrainInstrument
{
    private static readonly Meter Meter = Instruments.Meter;

    private static int _activeSessionsCount;

    public static readonly Counter<long> SessionsStarted = Meter.CreateCounter<long>(
        InstrumentNames.UserSessionsStarted,
        description: "Total number of user sessions started");

    public static readonly Counter<long> SessionsEnded = Meter.CreateCounter<long>(
        InstrumentNames.UserSessionsEnded,
        description: "Total number of user sessions ended");

    public static readonly Counter<long> Heartbeats = Meter.CreateCounter<long>(
        InstrumentNames.UserSessionHeartbeats,
        description: "Total number of heartbeats received");

    public static readonly Histogram<double> SessionDuration = Meter.CreateHistogram<double>(
        InstrumentNames.UserSessionDuration,
        unit: "s",
        description: "Duration of user sessions");

    public static readonly Counter<long> Expirations = Meter.CreateCounter<long>(
        InstrumentNames.UserSessionExpirations,
        description: "Total number of session expirations detected");

    public static readonly Counter<long> StatusChanges = Meter.CreateCounter<long>(
        InstrumentNames.UserStatusChanges,
        description: "Total number of status changes");

    // Local gauge for active sessions on this silo
    public static void IncrementActiveSession()
        => Interlocked.Increment(ref _activeSessionsCount);

    public static void DecrementActiveSession()
        => Interlocked.Decrement(ref _activeSessionsCount);

    private static readonly ObservableGauge<int> ActiveSessionsGauge = Meter.CreateObservableGauge(
        InstrumentNames.UserSessionsActive,
        () => _activeSessionsCount,
        description: "Current number of active sessions on this silo");
}