namespace Argon.Grains.Interfaces;

/// <summary>
/// Grain for mapping join request IDs to meeting IDs.
/// Key: requestId (Guid as string)
/// </summary>
[Alias("Argon.Grains.Interfaces.IJoinRequestGrain")]
public interface IJoinRequestGrain : IGrainWithStringKey
{
    /// <summary>
    /// Registers a join request for a meeting.
    /// </summary>
    [Alias("RegisterAsync")]
    Task RegisterAsync(Guid meetId, CancellationToken ct = default);

    /// <summary>
    /// Gets the meeting ID for this request.
    /// </summary>
    [Alias("GetMeetIdAsync")]
    Task<Guid?> GetMeetIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks this request as cancelled.
    /// </summary>
    [Alias("CancelAsync")]
    Task<bool> CancelAsync(CancellationToken ct = default);

    /// <summary>
    /// Checks if request is still active (not cancelled/expired).
    /// </summary>
    [Alias("IsActiveAsync")]
    Task<bool> IsActiveAsync(CancellationToken ct = default);
}
