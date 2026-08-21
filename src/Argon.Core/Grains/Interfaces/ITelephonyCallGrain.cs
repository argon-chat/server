namespace Argon.Core.Grains.Interfaces;

/// <summary>
/// Grain interface for managing outbound telephony calls via SIP/Twilio.
/// Grain key is the call ID.
/// </summary>
public interface ITelephonyCallGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Initiates an outbound call to a phone number.
    /// </summary>
    /// <param name="callerId">The user initiating the call.</param>
    /// <param name="phoneNumber">Target phone number in E.164 format.</param>
    /// <param name="centsPerMinute">Price per minute in cents.</param>
    /// <param name="correlationId">Correlation ID from dial check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating success or failure reason.</returns>
    Task<TelephonyCallResult> StartOutboundCallAsync(
        Guid callerId,
        string phoneNumber,
        long centsPerMinute,
        Guid correlationId,
        CancellationToken ct = default);

    /// <summary>
    /// Hangs up the call.
    /// </summary>
    /// <param name="userId">User requesting hangup.</param>
    /// <param name="reason">Reason for hangup.</param>
    /// <param name="ct">Cancellation token.</param>
    Task HangupAsync(Guid userId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Gets the current state of the call.
    /// </summary>
    Task<TelephonyCallState> GetStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Called when the remote party answers the call.
    /// </summary>
    Task OnRemoteAnsweredAsync(CancellationToken ct = default);

    /// <summary>
    /// Called when the call ends from the remote side.
    /// </summary>
    /// <param name="reason">Reason for call termination.</param>
    Task OnRemoteHangupAsync(string reason, CancellationToken ct = default);
}

public enum TelephonyCallStatus
{
    None,
    Initiating,
    Ringing,
    Connected,
    Ended,
    Failed
}

public sealed class TelephonyCallState
{
    public Guid                  CallId         { get; set; }
    public Guid                  CallerId       { get; set; }
    public string                PhoneNumber    { get; set; } = string.Empty;
    public string?               FromNumber     { get; set; }
    public TelephonyCallStatus   Status         { get; set; }
    public string                RoomName       { get; set; } = string.Empty;
    public string?               CallerToken    { get; set; }
    public string?               SipParticipantId { get; set; }
    public DateTimeOffset?       StartedAt      { get; set; }
    public DateTimeOffset?       ConnectedAt    { get; set; }
    public DateTimeOffset?       EndedAt        { get; set; }
    public string?               EndReason      { get; set; }
    public long                  CentsPerMinute { get; set; }
    public long                  TotalCharged   { get; set; }
}

public sealed record TelephonyCallResult(
    bool Success,
    Guid CallId,
    string? CallerToken,
    string? RoomName,
    string? Error);
