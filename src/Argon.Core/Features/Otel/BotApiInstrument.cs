namespace Argon;

using System.Diagnostics.Metrics;

public static class BotApiInstrument
{
    private static readonly Meter Meter = Instruments.Meter;

    // ── Event Publishing ─────────────────────────────────────────────

    public static readonly Counter<long> EventsPublished = Meter.CreateCounter<long>(
        InstrumentNames.BotEventsPublished,
        description: "Total number of bot events published to NATS");

    public static readonly Histogram<double> EventPublishDuration = Meter.CreateHistogram<double>(
        InstrumentNames.BotEventPublishDuration,
        unit: "ms",
        description: "Duration of bot event publish operations");

    public static readonly Counter<long> EventPublishErrors = Meter.CreateCounter<long>(
        InstrumentNames.BotEventPublishErrors,
        description: "Total number of bot event publish failures");

    // ── SSE Connections ──────────────────────────────────────────────

    public static readonly Counter<long> SseConnectionsOpened = Meter.CreateCounter<long>(
        InstrumentNames.BotSseConnectionsOpened,
        description: "Total number of bot SSE connections opened");

    public static readonly Counter<long> SseConnectionsClosed = Meter.CreateCounter<long>(
        InstrumentNames.BotSseConnectionsClosed,
        description: "Total number of bot SSE connections closed");

    private static int _activeSseConnections;

    public static void IncrementSseConnection()
        => Interlocked.Increment(ref _activeSseConnections);

    public static void DecrementSseConnection()
        => Interlocked.Decrement(ref _activeSseConnections);

    private static readonly ObservableGauge<int> ActiveSseConnectionsGauge = Meter.CreateObservableGauge(
        InstrumentNames.BotSseConnectionsActive,
        () => _activeSseConnections,
        description: "Current number of active bot SSE connections");

    public static readonly Counter<long> SseEventsDelivered = Meter.CreateCounter<long>(
        InstrumentNames.BotSseEventsDelivered,
        description: "Total number of bot SSE events delivered to clients");

    // ── Slash Commands ───────────────────────────────────────────────

    public static readonly Counter<long> CommandInvocations = Meter.CreateCounter<long>(
        InstrumentNames.BotCommandInvocations,
        description: "Total number of bot slash command invocations");

    public static readonly Histogram<double> CommandDispatchDuration = Meter.CreateHistogram<double>(
        InstrumentNames.BotCommandDispatchDuration,
        unit: "ms",
        description: "Duration of slash command dispatch");

    public static readonly Counter<long> CommandErrors = Meter.CreateCounter<long>(
        InstrumentNames.BotCommandErrors,
        description: "Total number of slash command invocation errors");
}
