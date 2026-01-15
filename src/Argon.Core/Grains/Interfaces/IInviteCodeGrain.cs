namespace Argon.Grains.Interfaces;

/// <summary>
/// Grain for mapping invite codes to meeting IDs.
/// Key: normalized invite code (without dashes)
/// </summary>
[Alias("Argon.Grains.Interfaces.IInviteCodeGrain")]
public interface IInviteCodeGrain : IGrainWithStringKey
{
    /// <summary>
    /// Registers an invite code for a meeting.
    /// </summary>
    [Alias("RegisterAsync")]
    Task RegisterAsync(Guid meetId, CancellationToken ct = default);

    /// <summary>
    /// Gets the meeting ID for this invite code.
    /// </summary>
    [Alias("GetMeetIdAsync")]
    Task<Guid?> GetMeetIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Invalidates this invite code.
    /// </summary>
    [Alias("InvalidateAsync")]
    Task InvalidateAsync(CancellationToken ct = default);
}
