namespace Argon.Grains.Interfaces;

using ConsoleContracts;

/// <summary>
/// The operator console's view of what users build: spaces, bots, dev teams and internal apps.
/// </summary>
/// <remarks>
/// Stateless, keyed by <see cref="Guid.Empty"/>; see <see cref="IAdminUsersGrain"/> for why the
/// console's queries live in grains. The platform flags on a space are not here — those belong to
/// <c>ISpaceGrain</c>, which owns the write, the cache drop and the broadcast.
/// </remarks>
[Alias("Argon.Grains.Interfaces.IAdminDirectoryGrain")]
public interface IAdminDirectoryGrain : IGrainWithGuidKey
{
    // ── spaces ───────────────────────────────────────────────────────────────────────────────

    [Alias(nameof(SearchSpaceAsync))]
    Task<AdminSpaceSearchResult> SearchSpaceAsync(string query, CancellationToken ct = default);

    /// <summary>The space card, or null when there is no such space.</summary>
    [Alias(nameof(GetSpaceCardAsync))]
    Task<AdminSpaceCard?> GetSpaceCardAsync(Guid spaceId, CancellationToken ct = default);

    [Alias(nameof(SpaceExistsAsync))]
    Task<bool> SpaceExistsAsync(Guid spaceId, CancellationToken ct = default);

    [Alias(nameof(GetSpaceMembersAsync))]
    Task<AdminSpaceMemberPage> GetSpaceMembersAsync(Guid spaceId, int offset, int limit, CancellationToken ct = default);

    // ── bots ─────────────────────────────────────────────────────────────────────────────────

    [Alias(nameof(SearchBotAsync))]
    Task<AdminBotSearchResult> SearchBotAsync(string query, CancellationToken ct = default);

    /// <summary>The bot card, or null when there is no such bot.</summary>
    [Alias(nameof(GetBotCardAsync))]
    Task<AdminBotCard?> GetBotCardAsync(Guid appId, CancellationToken ct = default);

    [Alias(nameof(SetBotVerifiedAsync))]
    Task<UserActionResult> SetBotVerifiedAsync(Guid appId, bool isVerified, CancellationToken ct = default);

    [Alias(nameof(SetBotMaxSpacesAsync))]
    Task<UserActionResult> SetBotMaxSpacesAsync(Guid appId, int maxSpaces, CancellationToken ct = default);

    [Alias(nameof(SetAppInternalAsync))]
    Task<UserActionResult> SetAppInternalAsync(Guid appId, bool isInternalApp, CancellationToken ct = default);

    [Alias(nameof(SetBotLifecycleStateAsync))]
    Task<UserActionResult> SetBotLifecycleStateAsync(Guid appId, AdminBotLifecycleState state, CancellationToken ct = default);

    // ── teams and internal apps ──────────────────────────────────────────────────────────────

    [Alias(nameof(SearchTeamAsync))]
    Task<AdminTeamSearchResult> SearchTeamAsync(string query, CancellationToken ct = default);

    /// <summary>The team card, or null when there is no such team.</summary>
    [Alias(nameof(GetTeamCardAsync))]
    Task<AdminTeamCard?> GetTeamCardAsync(Guid teamId, CancellationToken ct = default);

    [Alias(nameof(SearchInternalAppsAsync))]
    Task<InternalAppSearchResult> SearchInternalAppsAsync(string query, CancellationToken ct = default);
}
