namespace Argon.Grains.Instruments;

using Argon;
using System.Diagnostics.Metrics;

public static class AuthorizationGrainInstrument
{
    private static readonly Meter Meter = Instruments.Meter;

    public static readonly Counter<long> AuthorizationAttempts = Meter.CreateCounter<long>(
        InstrumentNames.AuthorizationAttempts,
        description: "Total number of user authorization attempts");

    public static readonly Histogram<double> AuthorizationDuration = Meter.CreateHistogram<double>(
        InstrumentNames.AuthorizationDuration,
        unit: "ms",
        description: "Duration of authorization operations");

    public static readonly Counter<long> UserRegistrations = Meter.CreateCounter<long>(
        InstrumentNames.UserRegistrations,
        description: "Total number of user registrations");

    public static readonly Histogram<double> UserRegistrationDuration = Meter.CreateHistogram<double>(
        InstrumentNames.UserRegistrationDuration,
        unit: "ms",
        description: "Duration of registration operations");

    public static readonly Counter<long> PasswordResets = Meter.CreateCounter<long>(
        InstrumentNames.PasswordResets,
        description: "Total number of password reset requests");

    public static readonly Histogram<double> PasswordResetDuration = Meter.CreateHistogram<double>(
        InstrumentNames.PasswordResetDuration,
        unit: "ms",
        description: "Duration of password reset operations");

    public static readonly Counter<long> ExternalAuthorizationAttempts = Meter.CreateCounter<long>(
        InstrumentNames.ExternalAuthorizationAttempts,
        description: "Total number of external authorization attempts");

    public static readonly Counter<long> AuthorizationOtpSent = Meter.CreateCounter<long>(
        InstrumentNames.AuthorizationOtpSent,
        description: "Total number of OTP sends during authorization flow");
}