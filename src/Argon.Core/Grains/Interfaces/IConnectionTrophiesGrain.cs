namespace Argon.Grains.Interfaces;

/// <summary>
/// Trophies paid out for what an external identity did — today the GitHub contributor coin.
/// </summary>
/// <remarks>
/// A stateless worker keyed <c>Guid.Empty</c>. The contributor set is cached across users; the
/// grant is idempotent per external identity through <c>connection_trophy_grants</c>, so the same
/// GitHub account never mints twice whichever Argon account holds it.
/// </remarks>
[Alias("Argon.Grains.Interfaces.IConnectionTrophiesGrain")]
public interface IConnectionTrophiesGrain : IGrainWithGuidKey
{
    /// <summary>True when the coin was granted now; false when not a contributor, or already paid.</summary>
    [Alias(nameof(CheckGitHubContributorAsync))]
    Task<bool> CheckGitHubContributorAsync(Guid userId, string gitHubId, CancellationToken ct = default);
}
