namespace Argon.Features.Aegis;

using Argon.Grains.Interfaces;
using Microsoft.Extensions.Caching.Hybrid;

/// <summary>
/// Everything the identity server has to look up, in front of the grains that own it.
/// </summary>
/// <remarks>
/// A single OAuth authorization asks the same handful of questions several times over — the app's
/// display info for the consent screen, its credentials for the redirect check, the operator record
/// for the step-up decision — and each one is a hop out of this process. What is cached is what does
/// not change during a sign-in; what depends on <i>which</i> user is asking is not cached at all,
/// because the answer would be wrong for the next caller rather than merely stale.
/// </remarks>
public interface IAegisDirectory
{
    Task<BotCredentialsInfo?> GetAppCredentialsAsync(string clientId, CancellationToken ct = default);

    /// <summary>
    /// The consent screen's view of an application, with <paramref name="requestedScopes"/> attached
    /// as asked for rather than as cached.
    /// </summary>
    Task<OAuthAppInfo?> GetAppInfoAsync(string clientId, IReadOnlyList<string> requestedScopes, CancellationToken ct = default);

    Task<AppLoginCheckInfo?> GetAppLoginCheckAsync(string clientId, CancellationToken ct = default);

    /// <summary>Whether this user may sign into this application at all.</summary>
    Task<LoginAllowedResult> CanSignInAsync(string clientId, Guid userId, CancellationToken ct = default);

    Task<UserBasicInfo?> GetUserAsync(Guid userId, CancellationToken ct = default);

    Task<string?> GetUserEmailAsync(Guid userId, CancellationToken ct = default);

    Task<Guid?> GetUserIdByEmailAsync(string email, CancellationToken ct = default);

    Task<OperatorBasicInfo?> GetOperatorAsync(Guid userId, CancellationToken ct = default);

    Task<OperatorAppAccessInfo?> GetOperatorAppAccessAsync(Guid operatorId, Guid appId, CancellationToken ct = default);

    Task<bool> HasAnyOperatorAppAccessAsync(Guid operatorId, CancellationToken ct = default);

    /// <summary>Every origin some registered application redirects to.</summary>
    Task<HashSet<string>> GetAllowedOriginsAsync(CancellationToken ct = default);
}

public sealed class AegisDirectory(IGrainFactory grains, HybridCache cache) : IAegisDirectory
{
    // An application's registration changes when a developer edits it, which is rare and never
    // urgent; anything about a person — their account, their operator grants — is held for a
    // shorter time, because those are permissions.
    private static readonly HybridCacheEntryOptions AppEntry = new()
    {
        Expiration           = TimeSpan.FromMinutes(5),
        LocalCacheExpiration = TimeSpan.FromMinutes(2)
    };

    private static readonly HybridCacheEntryOptions UserEntry = new()
    {
        Expiration           = TimeSpan.FromMinutes(2),
        LocalCacheExpiration = TimeSpan.FromMinutes(1)
    };

    private IAppsManagementGrain   Apps      => grains.GetGrain<IAppsManagementGrain>(Guid.Empty);
    private IDevTeamsGrain         Teams     => grains.GetGrain<IDevTeamsGrain>(Guid.Empty);
    private IIdentityDirectoryGrain Directory => grains.GetGrain<IIdentityDirectoryGrain>(Guid.Empty);

    public async Task<BotCredentialsInfo?> GetAppCredentialsAsync(string clientId, CancellationToken ct = default)
        => await cache.GetOrCreateAsync($"aegis:app:creds:{clientId}", (self: this, clientId),
            static (state, token) => new ValueTask<BotCredentialsInfo?>(
                state.self.Apps.GetCredentialsForBotAsync(state.clientId, token)),
            AppEntry, cancellationToken: ct);

    /// <remarks>
    /// The requested scopes are not part of the key and must not be part of the cached value: they
    /// come from the request being authorized right now, and caching them under the client id alone
    /// would show the second application's consent screen the first one's scope list.
    /// </remarks>
    public async Task<OAuthAppInfo?> GetAppInfoAsync(
        string clientId, IReadOnlyList<string> requestedScopes, CancellationToken ct = default)
    {
        var app = await cache.GetOrCreateAsync($"aegis:app:info:{clientId}", (self: this, clientId),
            static (state, token) => new ValueTask<OAuthAppInfo?>(
                state.self.Apps.GetOAuthAppInfoAsync(state.clientId, [], token)),
            AppEntry, cancellationToken: ct);

        return app is null ? null : app with { RequestedScopes = requestedScopes };
    }

    public async Task<AppLoginCheckInfo?> GetAppLoginCheckAsync(string clientId, CancellationToken ct = default)
        => await cache.GetOrCreateAsync($"aegis:app:login:{clientId}", (self: this, clientId),
            static (state, token) => new ValueTask<AppLoginCheckInfo?>(
                state.self.Teams.GetAppLoginCheckInfoAsync(state.clientId, token)),
            AppEntry, cancellationToken: ct);

    // Not cached: the answer is about this user and this app together, and the policy behind it
    // reads team membership and the mailbox domain.
    public Task<LoginAllowedResult> CanSignInAsync(string clientId, Guid userId, CancellationToken ct = default)
        => Apps.CanBeLoginForAppAsync(clientId, userId, ct);

    public async Task<UserBasicInfo?> GetUserAsync(Guid userId, CancellationToken ct = default)
        => await cache.GetOrCreateAsync($"aegis:user:basic:{userId}", (self: this, userId),
            static (state, token) => new ValueTask<UserBasicInfo?>(
                state.self.Directory.GetUserBasicInfoAsync(state.userId, token)),
            UserEntry, cancellationToken: ct);

    public Task<string?> GetUserEmailAsync(Guid userId, CancellationToken ct = default)
        => Teams.GetUserEmailAsync(userId, ct);

    public Task<Guid?> GetUserIdByEmailAsync(string email, CancellationToken ct = default)
        => Directory.GetUserIdByEmailAsync(email, ct);

    public async Task<OperatorBasicInfo?> GetOperatorAsync(Guid userId, CancellationToken ct = default)
        => await cache.GetOrCreateAsync($"aegis:user:operator:{userId}", (self: this, userId),
            static (state, token) => new ValueTask<OperatorBasicInfo?>(
                state.self.Directory.GetUserOperatorInfoAsync(state.userId, token)),
            UserEntry, cancellationToken: ct);

    public async Task<OperatorAppAccessInfo?> GetOperatorAppAccessAsync(
        Guid operatorId, Guid appId, CancellationToken ct = default)
        => await cache.GetOrCreateAsync($"aegis:operator:app-access:{operatorId}:{appId}", (self: this, operatorId, appId),
            static (state, token) => new ValueTask<OperatorAppAccessInfo?>(
                state.self.Directory.GetOperatorAppAccessAsync(state.operatorId, state.appId, token)),
            UserEntry, cancellationToken: ct);

    public async Task<bool> HasAnyOperatorAppAccessAsync(Guid operatorId, CancellationToken ct = default)
        => await cache.GetOrCreateAsync($"aegis:operator:has-app-access:{operatorId}", (self: this, operatorId),
            static (state, token) => new ValueTask<bool>(
                state.self.Directory.GetOperatorHasAnyAppAccessAsync(state.operatorId, token)),
            UserEntry, cancellationToken: ct);

    public async Task<HashSet<string>> GetAllowedOriginsAsync(CancellationToken ct = default)
        => await cache.GetOrCreateAsync("aegis:cors:allowed-origins", this,
            static (self, token) => new ValueTask<HashSet<string>>(self.Teams.GetAllAllowedOriginsAsync(token)),
            AppEntry, cancellationToken: ct) ?? [];
}
