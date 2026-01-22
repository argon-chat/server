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

    public static readonly Counter<long> LinkedMeetingsCreated = Meter.CreateCounter<long>(
        InstrumentNames.ChannelLinkedMeetingsCreated,
        description: "Total number of linked meetings created");

    public static readonly Counter<long> LinkedMeetingsEnded = Meter.CreateCounter<long>(
        InstrumentNames.ChannelLinkedMeetingsEnded,
        description: "Total number of linked meetings ended");

    public static readonly Counter<long> TypingEvents = Meter.CreateCounter<long>(
        InstrumentNames.ChannelTypingEvents,
        description: "Total number of typing events emitted");

    public static readonly Counter<long> MemberKicks = Meter.CreateCounter<long>(
        InstrumentNames.ChannelMemberKicks,
        description: "Total number of channel member kicks");
}