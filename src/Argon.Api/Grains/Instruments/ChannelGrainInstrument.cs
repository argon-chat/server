namespace Argon.Grains.Instruments;

using Argon;
using System.Diagnostics.Metrics;

public static class ChannelGrainInstrument
{
    private static readonly Meter Meter = Instruments.Meter;

    public static readonly Counter<long> MessagesSent = Meter.CreateCounter<long>(
        InstrumentNames.ChannelMessagesSent,
        description: "Total number of messages sent in channels");

    public static readonly Histogram<double> MessageSendDuration = Meter.CreateHistogram<double>(
        InstrumentNames.ChannelMessageSendDuration,
        unit: "ms",
        description: "Duration of message send operations");

    public static readonly Counter<long> VoiceJoins = Meter.CreateCounter<long>(
        InstrumentNames.ChannelVoiceJoins,
        description: "Total number of voice channel joins");

    public static readonly Counter<long> VoiceLeaves = Meter.CreateCounter<long>(
        InstrumentNames.ChannelVoiceLeaves,
        description: "Total number of voice channel leaves");

    public static readonly Histogram<double> VoiceSessionDuration = Meter.CreateHistogram<double>(
        InstrumentNames.ChannelVoiceSessionDuration,
        unit: "s",
        description: "Duration of voice sessions");

    public static readonly Gauge<int> VoiceActiveUsers = Meter.CreateGauge<int>(
        InstrumentNames.ChannelVoiceActiveUsers,
        description: "Current number of users in voice channels");

    public static readonly Counter<long> RecordingsStarted = Meter.CreateCounter<long>(
        InstrumentNames.ChannelRecordingsStarted,
        description: "Total number of channel recordings started");

    public static readonly Counter<long> RecordingsStopped = Meter.CreateCounter<long>(
        InstrumentNames.ChannelRecordingsStopped,
        description: "Total number of channel recordings stopped");

    public static readonly Counter<long> TypingEvents = Meter.CreateCounter<long>(
        InstrumentNames.ChannelTypingEvents,
        description: "Total number of typing events emitted");

    public static readonly Counter<long> MemberKicks = Meter.CreateCounter<long>(
        InstrumentNames.ChannelMemberKicks,
        description: "Total number of channel member kicks");

    public static readonly Counter<long> ReactionsAdded = Meter.CreateCounter<long>(
        InstrumentNames.ChannelReactionsAdded,
        description: "Total number of reactions added");

    public static readonly Counter<long> ReactionsRemoved = Meter.CreateCounter<long>(
        InstrumentNames.ChannelReactionsRemoved,
        description: "Total number of reactions removed");

    // The two below are only ever read as a ratio against MessagesSent, so the queries that make
    // sense of them are documented here beside all three rather than split across InstrumentNames.
    private const string LastMessageFlushesName  = "argon-channel-last-message-flushes";
    private const string LastMessageAbsorbedName = "argon-channel-last-message-absorbed";

    /// <summary>
    /// Durable writes of a channel's high-water mark — one per flush interval that had traffic, not
    /// one per message.
    /// Tags: result (written, failed)
    /// </summary>
    /// <remarks>
    /// <para><strong>Grafana — coalescing ratio:</strong>
    /// <c>sum(rate(argon_channel_last_message_flushes[$__rate_interval])) / sum(rate(argon_channel_messages_sent[$__rate_interval]))</c></para>
    /// <para>
    /// Near 1 means every message is still costing a write: the flush timer is not firing and
    /// activations have fallen back to per-message behaviour. A healthy busy channel sits far below
    /// it, because a whole flush interval of sends collapses into one write.
    /// </para>
    /// <para><strong>Grafana — failed flushes:</strong>
    /// <c>sum(rate(argon_channel_last_message_flushes{result="failed"}[$__rate_interval]))</c>.
    /// Worth an alert: a failed flush no longer heals itself the way a failed per-message write did,
    /// it is only retried while the activation lives.
    /// </para>
    /// </remarks>
    public static readonly Counter<long> LastMessageFlushes = Meter.CreateCounter<long>(
        LastMessageFlushesName,
        description: "Coalesced writes of the channel last-message high-water mark");

    /// <summary>
    /// Messages that raised the high-water mark without buying a write of their own, because one was
    /// already owed.
    /// </summary>
    /// <remarks>
    /// <para><strong>Grafana — writes avoided:</strong>
    /// <c>sum(increase(argon_channel_last_message_absorbed[$__range]))</c>, which is exactly the
    /// number of database writes this coalescing removed over the range.</para>
    /// </remarks>
    public static readonly Counter<long> LastMessageAbsorbed = Meter.CreateCounter<long>(
        LastMessageAbsorbedName,
        description: "Messages folded into an already-pending channel high-water mark write");
}