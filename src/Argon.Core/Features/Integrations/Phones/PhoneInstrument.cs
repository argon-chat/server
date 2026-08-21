namespace Argon.Features.Integrations.Phones;

using Argon;
using System.Diagnostics.Metrics;

public static class PhoneInstrument
{
    private static readonly Meter Meter = Instruments.Meter;

    public static readonly Counter<long> VerificationSent = Meter.CreateCounter<long>(
        InstrumentNames.PhoneVerificationSent,
        description: "Total number of phone verification codes sent");

    public static readonly Counter<long> VerificationChecks = Meter.CreateCounter<long>(
        InstrumentNames.PhoneVerificationChecks,
        description: "Total number of phone verification checks performed");

    public static readonly Histogram<double> SendDuration = Meter.CreateHistogram<double>(
        InstrumentNames.PhoneVerificationSendDuration,
        unit: "ms",
        description: "Duration of phone verification send operations");

    public static readonly Histogram<double> CheckDuration = Meter.CreateHistogram<double>(
        InstrumentNames.PhoneVerificationCheckDuration,
        unit: "ms",
        description: "Duration of phone verification check operations");

    public static readonly Counter<long> TelegramSendAbilityChecks = Meter.CreateCounter<long>(
        InstrumentNames.PhoneTelegramSendAbilityChecks,
        description: "Total number of Telegram send ability checks");

    public static readonly Gauge<decimal> TelegramBalance = Meter.CreateGauge<decimal>(
        InstrumentNames.PhoneTelegramBalance,
        description: "Telegram Gateway remaining balance");

    public static readonly Counter<decimal> VerificationCost = Meter.CreateCounter<decimal>(
        InstrumentNames.PhoneVerificationCost,
        description: "Total cost of phone verification requests");

    public static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>(
        InstrumentNames.PhoneVerificationFallbacks,
        description: "Total number of phone verification fallback events");
}