namespace Argon.Grains.Interfaces;

using AccountContracts;
using Argon.Core.Entities.Data;
using BotLifecycleState = Argon.Core.Entities.Data.BotLifecycleState;

/// <summary>
/// Everything the developer account console reads and writes: dev teams, their membership and
/// invites, and the applications those teams own.
/// </summary>
/// <remarks>
/// Stateless — every call goes straight to the database, so use <see cref="Guid.Empty"/> as the key
/// and let the worker pool size itself. It exists as a grain rather than a repository service
/// because database access lives in grains; the console is a client role and never opens a
/// connection of its own.
/// <para>
/// The returned types are the console's own Ion contracts. They cross the grain boundary intact —
/// the Orleans serializer is configured with the Ion converters — and inventing a parallel set of
/// records to map twice would buy nothing.
/// </para>
/// </remarks>
[Alias("Argon.Grains.Interfaces.IDevTeamsGrain")]
public interface IDevTeamsGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetMyTeamsAsync))]
    Task<List<TeamShortDetails>> GetMyTeamsAsync(Guid userId, CancellationToken ct = default);

    [Alias(nameof(GetTeamDetailsAsync))]
    Task<TeamDetails> GetTeamDetailsAsync(Guid teamId, CancellationToken ct = default);

    [Alias(nameof(GetTeamNameAsync))]
    Task<string?> GetTeamNameAsync(Guid teamId, CancellationToken ct = default);

    [Alias(nameof(CreateTeamAsync))]
    Task<TeamDetails> CreateTeamAsync(Guid ownerId, string name, CancellationToken ct = default);

    [Alias(nameof(IsUserInTeamAsync))]
    Task<bool> IsUserInTeamAsync(Guid userId, Guid teamId, CancellationToken ct = default);

    [Alias(nameof(IsUserTeamOwnerAsync))]
    Task<bool> IsUserTeamOwnerAsync(Guid userId, Guid teamId, CancellationToken ct = default);

    [Alias(nameof(GetUserEmailAsync))]
    Task<string?> GetUserEmailAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Every origin any application has registered a redirect under.
    /// </summary>
    /// <remarks>
    /// The identity server answers cross-origin requests from whatever site an application is served
    /// from, so its CORS check is a lookup rather than a list. Whole-set rather than per-origin: the
    /// caller asks once per request and the answer is the same for all of them, so caching one set
    /// hits where caching one origin at a time would miss on every new site.
    /// </remarks>
    [Alias(nameof(GetAllAllowedOriginsAsync))]
    Task<HashSet<string>> GetAllAllowedOriginsAsync(CancellationToken ct = default);

    // ── invites ──────────────────────────────────────────────────────────────────────────────

    [Alias(nameof(GetTeamInvitesAsync))]
    Task<List<TeamInviteInfo>> GetTeamInvitesAsync(Guid teamId, CancellationToken ct = default);

    [Alias(nameof(GetMyInvitesAsync))]
    Task<List<MyInvitesInfo>> GetMyInvitesAsync(Guid userId, CancellationToken ct = default);

    [Alias(nameof(InviteUserToTeamAsync))]
    Task<InviteUserError> InviteUserToTeamAsync(Guid teamId, Guid fromUserId, string username, TimeSpan ttl, CancellationToken ct = default);

    [Alias(nameof(AcceptInviteAsync))]
    Task AcceptInviteAsync(Guid userId, Guid teamId, CancellationToken ct = default);

    [Alias(nameof(DeclineTeamInviteAsync))]
    Task DeclineTeamInviteAsync(Guid userId, Guid teamId, CancellationToken ct = default);

    // ── applications ─────────────────────────────────────────────────────────────────────────

    [Alias(nameof(CreateBotAppAsync))]
    Task<AppDetails> CreateBotAppAsync(Guid teamId, string name, string username, CancellationToken ct = default);

    [Alias(nameof(CreateClientAppAsync))]
    Task<AppDetails> CreateClientAppAsync(Guid teamId, string name, ClientAppPlatform platform, CancellationToken ct = default);

    [Alias(nameof(CheckUsernameForBotAsync))]
    Task<CheckBotUsernameValid> CheckUsernameForBotAsync(string username, CancellationToken ct = default);

    [Alias(nameof(GetAppDetailsAsync))]
    Task<AppDetails> GetAppDetailsAsync(Guid teamId, Guid appId, CancellationToken ct = default);

    [Alias(nameof(GetAppDetailsByClientIdAsync))]
    Task<AppDetails?> GetAppDetailsByClientIdAsync(string clientId, CancellationToken ct = default);

    [Alias(nameof(GetAppLoginCheckInfoAsync))]
    Task<AppLoginCheckInfo?> GetAppLoginCheckInfoAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// The OAuth credentials of an application — a client app or a bot — with its scopes filtered
    /// down to the ones it is currently entitled to. A scope stays in <c>RequiredScopes</c> after
    /// the application loses eligibility for it — losing verification re-locks
    /// <c>offline_access</c>, for one — so filtering here is what stops the token endpoint from
    /// honouring it.
    /// </summary>
    /// <remarks>
    /// Named for bots because that is what it once resolved, and kept that way because the name is
    /// the Orleans alias: renaming it would break a call in flight across a rolling deployment.
    /// </remarks>
    [Alias(nameof(GetBotCredentialsAsync))]
    Task<BotCredentialsInfo?> GetBotCredentialsAsync(string clientId, CancellationToken ct = default);

    [Alias(nameof(GetAppOAuthDisplayInfoAsync))]
    Task<AppOAuthDisplayInfo?> GetAppOAuthDisplayInfoAsync(string clientId, CancellationToken ct = default);

    [Alias(nameof(RegenerateBotTokenAsync))]
    Task<string> RegenerateBotTokenAsync(Guid teamId, Guid appId, CancellationToken ct = default);

    [Alias(nameof(UpdateScopeAsync))]
    Task UpdateScopeAsync(Guid teamId, Guid appId, ScopeKeyValue scope, CancellationToken ct = default);

    [Alias(nameof(AddRedirectAsync))]
    Task<AddRedirectResult> AddRedirectAsync(Guid teamId, Guid appId, string redirect, CancellationToken ct = default);

    [Alias(nameof(RemoveRedirectAsync))]
    Task RemoveRedirectAsync(Guid teamId, Guid appId, string redirect, CancellationToken ct = default);

    [Alias(nameof(SetBotLifecycleAsync))]
    Task SetBotLifecycleAsync(Guid teamId, Guid appId, BotLifecycleState state, CancellationToken ct = default);

    [Alias(nameof(UpdateBotEntitlementsAsync))]
    Task UpdateBotEntitlementsAsync(Guid teamId, Guid appId, ArgonEntitlement entitlements, CancellationToken ct = default);

    [Alias(nameof(SetBotOAuthAsync))]
    Task SetBotOAuthAsync(Guid teamId, Guid appId, bool enabled, CancellationToken ct = default);
}

/// <summary>
/// What deciding whether a user may sign into an application needs to know.
/// </summary>
/// <param name="AppId">The application itself, which per-app operator access is keyed by.</param>
/// <param name="TeamId">The team that owns the app.</param>
/// <param name="IsInternalApp">Whether this is an internal app (client apps only).</param>
/// <param name="IsPublic">Whether the app is public.</param>
/// <param name="IsVerified">Whether the app is verified.</param>
public record AppLoginCheckInfo(Guid AppId, Guid TeamId, bool IsInternalApp, bool IsPublic, bool IsVerified);

/// <summary>
/// What the OAuth consent screen shows about an application.
/// </summary>
public record AppOAuthDisplayInfo(
    Guid AppId,
    string AppName,
    string? Description,
    string? AvatarFileId,
    string? WebsiteUrl,
    bool IsVerified,
    bool IsInternalApp,
    Guid TeamId);
