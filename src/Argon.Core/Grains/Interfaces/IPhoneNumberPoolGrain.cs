namespace Argon.Core.Grains.Interfaces;

/// <summary>
/// Grain interface for managing pool of phone numbers for outbound SIP calls.
/// Singleton grain (key = 0).
/// </summary>
public interface IPhoneNumberPoolGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Acquires a phone number for an outbound call.
    /// </summary>
    /// <param name="callId">The call ID that will use this number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Phone number in E.164 format, or null if pool is empty.</returns>
    Task<string?> AcquirePhoneAsync(Guid callId, CancellationToken ct = default);

    /// <summary>
    /// Releases a phone number back to the pool.
    /// </summary>
    /// <param name="callId">The call ID that was using this number.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReleasePhoneAsync(Guid callId, CancellationToken ct = default);

    /// <summary>
    /// Refreshes the pool by fetching available numbers from Twilio.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task RefreshPoolAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets current pool statistics.
    /// </summary>
    Task<PhonePoolStats> GetStatsAsync(CancellationToken ct = default);
}

public sealed record PhonePoolStats(
    int TotalNumbers,
    int AvailableNumbers,
    int InUseNumbers);
