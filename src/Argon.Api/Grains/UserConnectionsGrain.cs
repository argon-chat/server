namespace Argon.Grains;

using Argon.Core.Features.CoreLogic.Privacy;
using Argon.Core.Features.Logic;
using Argon.Core.Features.Transport;
using Argon.Features.EF;
using Argon.Features.Integrations.Connections;
using Argon.Grains.Interfaces;
using Core.Entities.Data;
using ion.runtime;
// The Ion message, not SignalR's Microsoft.AspNetCore.Http.Connections.ConnectionOptions, which the
// Api project imports globally.
using ConnectionOptions = ArgonContracts.ConnectionOptions;

/// <inheritdoc cref="IUserConnectionsGrain"/>
public sealed class UserConnectionsGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IConnectionProviderRegistry providers,
    TokenSealer sealer,
    ConnectionHandshakeStore store,
    IOptions<ConnectionsOptions> options,
    ISystemNotificationService notifications,
    IUserPresenceService presence,
    AppHubServer appHubServer,
    ILogger<UserConnectionsGrain> logger) : Grain, IUserConnectionsGrain
{
    private Guid UserId => this.GetPrimaryKey();

    /// <summary>Spotify and Twitch: the providers with a poller a session attach may wake.</summary>
    private static bool HasStatusPoller(ConnectionProvider provider)
        => provider is ConnectionProvider.SPOTIFY or ConnectionProvider.TWITCH;

    /// <summary>Mirrors "shown as status" into the presence index the session grain reads on attach.</summary>
    private async Task MarkStatusProviderAsync(ConnectionProvider provider, bool shown)
    {
        if (!HasStatusPoller(provider))
            return;

        try
        {
            await presence.SetStatusProviderAsync(UserId, ConnectionProviders.Slug(provider), shown);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "The status index of {UserId} could not be updated for {Provider}", UserId, provider);
        }
    }

    // ── reads ────────────────────────────────────────────────────────────────────────────────

    public async Task<List<UserConnection>> GetMineAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        return (await RowsAsync(ctx, ct)).Select(ToDto).ToList();
    }

    public async Task<List<ProfileConnection>> GetVisibleForAsync(Guid viewerId, CancellationToken ct = default)
    {
        if (viewerId != UserId
         && !await GrainFactory.GetGrain<IPrivacyPolicyGrain>(UserId).EvaluateAsync(viewerId, PrivacyKeys.ConnectionsVisibility, null))
            return [];

        await using var ctx = await context.CreateDbContextAsync(ct);

        return (await RowsAsync(ctx, ct))
           .Where(r => r.DisplayOnProfile && r.Status != ConnectionStatus.SUSPENDED)
           .Select(ToProfile)
           .ToList();
    }

    public async Task<ExistingConnection?> GetExistingAsync(ConnectionProvider provider, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserConnections.AsNoTracking()
           .Where(x => x.UserId == UserId && x.Provider == provider)
           .Select(x => new { x.ExternalId, x.ExternalName, x.Status })
           .FirstOrDefaultAsync(ct);

        return row is null ? null : new ExistingConnection(row.ExternalId, row.ExternalName, row.Status);
    }

    // ── link / unlink ────────────────────────────────────────────────────────────────────────

    public async Task<LinkOutcome> LinkAsync(LinkRequest request, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var now      = DateTimeOffset.UtcNow;
        var identity = request.Identity;

        var existing = await ctx.UserConnections.FirstOrDefaultAsync(x => x.UserId == UserId && x.Provider == request.Provider, ct);

        if (existing is { Status: ConnectionStatus.SUSPENDED })
            return LinkOutcome.Suspended;

        if (existing is not null && existing.ExternalId != identity.ExternalId && !request.Replace)
            return LinkOutcome.ProviderAlreadyLinked;

        var heldElsewhere = await ctx.UserConnections.AsNoTracking()
           .AnyAsync(x => x.Provider == request.Provider && x.ExternalId == identity.ExternalId && x.UserId != UserId, ct);

        if (heldElsewhere)
            return LinkOutcome.AlreadyLinkedElsewhere;

        var row = existing ?? new UserConnectionEntity
        {
            Id           = ArgonId.New(),
            UserId       = UserId,
            Provider     = request.Provider,
            ExternalId   = identity.ExternalId,
            ExternalName = identity.Name,
            CreatedAt    = now
        };

        row.ExternalId           = identity.ExternalId;
        row.ExternalName         = Cap(identity.Name, 128);
        row.ExternalUrl          = Cap(identity.Url, 512);
        row.AvatarUrl            = Cap(identity.AvatarUrl, 1024);
        row.Scopes               = Cap(request.Scopes, 1024) ?? "";
        row.Details              = request.Details;
        row.DetailsRefreshedAt   = now;
        row.Status               = ConnectionStatus.ACTIVE;
        row.LastError            = null;
        row.LastErrorAt          = null;
        row.UpdatedAt            = now;
        row.AccessTokenExpiresAt = request.Token?.ExpiresAt;
        row.SealedTokens         = request.Token is { } token ? sealer.Seal(token, row.Id, row.Provider) : null;
        row.TokenKeyVersion      = request.Token is null ? 0 : sealer.CurrentVersion;

        if (existing is null)
            ctx.UserConnections.Add(row);

        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            // Lost the race to another account linking the same external identity.
            return LinkOutcome.AlreadyLinkedElsewhere;
        }

        logger.LogInformation("User {UserId} linked {Provider} account {ExternalId}", UserId, request.Provider, identity.ExternalId);

        await PublishAsync(ctx, ct);
        await AfterLinkAsync(row, ct);
        await PokeStatusPollerAsync(row);

        return LinkOutcome.Linked;
    }

    public async Task<bool> UnlinkAsync(ConnectionProvider provider, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserConnections.FirstOrDefaultAsync(x => x.UserId == UserId && x.Provider == provider, ct);

        if (row is null)
            return false;

        await RevokeQuietlyAsync(row, ct);

        ctx.UserConnections.Remove(row);
        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("User {UserId} unlinked {Provider}", UserId, provider);

        await PublishAsync(ctx, ct);
        await MarkStatusProviderAsync(provider, shown: false);

        switch (provider)
        {
            case ConnectionProvider.SPOTIFY:
                await GrainFactory.GetGrain<ISpotifyPresenceGrain>(UserId).StopAsync();
                break;
            case ConnectionProvider.TWITCH:
                await TwitchSubscriptionsAsync(row.ExternalId, wanted: false);
                await GrainFactory.GetGrain<ITwitchPresenceGrain>(UserId).StopAsync();
                break;
        }

        return true;
    }

    public async Task PurgeAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var rows = await ctx.UserConnections.Where(x => x.UserId == UserId).ToListAsync(ct);

        foreach (var row in rows)
        {
            await RevokeQuietlyAsync(row, ct);
            await MarkStatusProviderAsync(row.Provider, shown: false);

            if (row.Provider == ConnectionProvider.TWITCH)
                await TwitchSubscriptionsAsync(row.ExternalId, wanted: false);
        }

        await ctx.UserConnections.Where(x => x.UserId == UserId).ExecuteDeleteAsync(ct);
    }

    // ── operators ────────────────────────────────────────────────────────────────────────────

    public async Task<List<ConsoleContracts.AdminUserConnection>> GetForOperatorAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        return (await RowsAsync(ctx, ct))
           .Select(r => new ConsoleContracts.AdminUserConnection(r.Provider, r.ExternalId, r.ExternalName, r.ExternalUrl, r.Status,
                r.DisplayOnProfile, r.DisplayAsStatus, r.Scopes, r.CreatedAt, r.DetailsRefreshedAt, r.LastError, r.LastErrorAt))
           .ToList();
    }

    public async Task<bool> SetSuspendedAsync(ConnectionProvider provider, bool suspended, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserConnections.FirstOrDefaultAsync(x => x.UserId == UserId && x.Provider == provider, ct);

        if (row is null)
            return false;

        row.Status = suspended
            ? ConnectionStatus.SUSPENDED
            : row.SealedTokens is not null || providers.Get(provider) is { KeepsTokens: false }
                ? ConnectionStatus.ACTIVE
                : ConnectionStatus.NEEDS_REAUTH;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("{Provider} connection of {UserId} is now {Status} by an operator", provider, UserId, row.Status);

        await PublishAsync(ctx, ct);
        await PokeStatusPollerAsync(row);

        return true;
    }

    // ── options / details ────────────────────────────────────────────────────────────────────

    public async Task<Either<UserConnection, ConnectionError>> UpdateOptionsAsync(ConnectionProvider provider, IonPartial<ConnectionOptions> patch,
        CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserConnections.FirstOrDefaultAsync(x => x.UserId == UserId && x.Provider == provider, ct);

        if (row is null)
            return ConnectionError.NOT_LINKED;

        var capabilities = providers.Get(provider)?.Capabilities ?? ConnectionCapability.NONE;
        var unsupported  = false;

        patch.On(x => x.displayOnProfile,
            onModified: v => row.DisplayOnProfile = v,
            onRemoved: () => row.DisplayOnProfile = true);
        patch.On(x => x.showDetails,
            onModified: v => row.ShowDetails = v,
            onRemoved: () => row.ShowDetails = true);
        patch.On(x => x.displayAsStatus,
            onModified: v =>
            {
                if (capabilities.HasFlag(ConnectionCapability.STATUS)) row.DisplayAsStatus = v;
                else if (v) unsupported = true;
            },
            onRemoved: () => row.DisplayAsStatus = false);
        patch.On(x => x.allowListenAlong,
            onModified: v =>
            {
                if (capabilities.HasFlag(ConnectionCapability.LISTEN_ALONG)) row.AllowListenAlong = v;
                else if (!v) unsupported = true;
            },
            onRemoved: () => row.AllowListenAlong = true);

        if (unsupported)
            return ConnectionError.OPTION_NOT_SUPPORTED;

        row.UpdatedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(ct);

        await PublishAsync(ctx, ct);
        await PokeStatusPollerAsync(row);

        return ToDto(row);
    }

    /// <summary>
    /// Tells the status poller that its grant changed. One-way calls, because the poller may be
    /// awaiting this grain at this very moment; an awaited call back would deadlock.
    /// </summary>
    private async Task PokeStatusPollerAsync(UserConnectionEntity row)
    {
        var wanted = row.Status == ConnectionStatus.ACTIVE && row.DisplayAsStatus;

        await MarkStatusProviderAsync(row.Provider, wanted);

        switch (row.Provider)
        {
            case ConnectionProvider.SPOTIFY:
            {
                var poller = GrainFactory.GetGrain<ISpotifyPresenceGrain>(UserId);

                if (wanted) await poller.WakeAsync();
                else await poller.StopAsync();
                break;
            }
            case ConnectionProvider.TWITCH:
            {
                await TwitchSubscriptionsAsync(row.ExternalId, wanted);

                var poller = GrainFactory.GetGrain<ITwitchPresenceGrain>(UserId);

                if (wanted) await poller.WakeAsync();
                else await poller.StopAsync();
                break;
            }
        }
    }

    /// <summary>EventSub follows the option: subscribed while the stream is shown as status, dropped when not. Best effort.</summary>
    private async Task TwitchSubscriptionsAsync(string broadcasterId, bool wanted)
    {
        try
        {
            var eventSub = GrainFactory.GetGrain<ITwitchEventSubGrain>(Guid.Empty);

            if (wanted) await eventSub.EnsureSubscriptionsAsync(broadcasterId);
            else await eventSub.RemoveSubscriptionsAsync(broadcasterId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Twitch EventSub subscriptions for {UserId} could not be {Action}", UserId, wanted ? "created" : "removed");
        }
    }

    public async Task<Either<UserConnection, ConnectionError>> RefreshDetailsAsync(ConnectionProvider provider, bool force, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserConnections.FirstOrDefaultAsync(x => x.UserId == UserId && x.Provider == provider, ct);

        if (row is null)
            return ConnectionError.NOT_LINKED;

        if (providers.Get(provider) is not { } adapter)
            return ConnectionError.PROVIDER_ERROR;

        if (!adapter.Capabilities.HasFlag(ConnectionCapability.DETAILS) || row.Status == ConnectionStatus.SUSPENDED)
            return ToDto(row);

        if (row.Status == ConnectionStatus.NEEDS_REAUTH)
            return ConnectionError.NEEDS_REAUTH;

        if (!force && !await store.TryAcquireRefreshAsync(UserId, provider, ct))
            return ConnectionError.COOLDOWN;

        var (token, upkeep) = await OpenTokenAsync(ctx, row, adapter, ct);

        if (adapter.KeepsTokens && token is null)
        {
            if (upkeep is TokenUpkeep.Failed)
                return ConnectionError.PROVIDER_ERROR;

            await PublishAsync(ctx, ct);
            return ConnectionError.NEEDS_REAUTH;
        }

        try
        {
            var snapshot = await adapter.FetchAsync(new ConnectionIdentity(row.ExternalId, row.ExternalName, row.ExternalUrl, row.AvatarUrl), token, ct);

            // The id is the identity; a provider handing back another one is answering about somebody
            // else, and the row keeps what it has.
            if (snapshot.Identity.ExternalId == row.ExternalId)
            {
                row.ExternalName = Cap(snapshot.Identity.Name, 128) ?? row.ExternalName;
                row.ExternalUrl  = Cap(snapshot.Identity.Url, 512);
                row.AvatarUrl    = Cap(snapshot.Identity.AvatarUrl, 1024);
            }

            row.Details            = snapshot.Details.ToList();
            row.DetailsRefreshedAt = DateTimeOffset.UtcNow;
            row.LastError          = null;
            row.LastErrorAt        = null;
            row.UpdatedAt          = DateTimeOffset.UtcNow;

            await ctx.SaveChangesAsync(ct);
        }
        catch (ProviderCallException e) when (e.IsUnauthorized)
        {
            await MarkNeedsReauthAsync(ctx, row, "the provider no longer accepts the token", ct);
            await PublishAsync(ctx, ct);
            return ConnectionError.NEEDS_REAUTH;
        }
        catch (Exception e) when (e is ProviderCallException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Refreshing {Provider} details for {UserId} failed", provider, UserId);

            row.LastError   = Cap(e.Message, 512);
            row.LastErrorAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync(ct);

            return ConnectionError.PROVIDER_ERROR;
        }

        await PublishAsync(ctx, ct);
        await AfterLinkAsync(row, ct);

        return ToDto(row);
    }

    // ── tokens ───────────────────────────────────────────────────────────────────────────────

    public async Task<ProviderToken?> GetTokenAsync(ConnectionProvider provider, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserConnections.FirstOrDefaultAsync(x => x.UserId == UserId && x.Provider == provider, ct);

        if (row is null || row.Status != ConnectionStatus.ACTIVE || providers.Get(provider) is not { } adapter)
            return null;

        var (token, upkeep) = await OpenTokenAsync(ctx, row, adapter, ct);

        if (upkeep is TokenUpkeep.NeedsReauth)
            await PublishAsync(ctx, ct);

        return token;
    }

    public async Task<ActivityGrant?> GetActivityGrantAsync(ConnectionProvider provider, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserConnections.FirstOrDefaultAsync(x => x.UserId == UserId && x.Provider == provider, ct);

        if (row is null || row.Status != ConnectionStatus.ACTIVE || !row.DisplayAsStatus || providers.Get(provider) is not { } adapter)
            return null;

        var (token, upkeep) = await OpenTokenAsync(ctx, row, adapter, ct);

        if (upkeep is TokenUpkeep.NeedsReauth)
            await PublishAsync(ctx, ct);

        return token is null ? null : new ActivityGrant(token, row.AllowListenAlong);
    }

    public async Task<ListenerGrant?> GetListenerGrantAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserConnections.FirstOrDefaultAsync(x => x.UserId == UserId && x.Provider == ConnectionProvider.SPOTIFY, ct);

        if (row is null || row.Status != ConnectionStatus.ACTIVE || providers.Get(ConnectionProvider.SPOTIFY) is not { } adapter)
            return null;

        var (token, upkeep) = await OpenTokenAsync(ctx, row, adapter, ct);

        if (upkeep is TokenUpkeep.NeedsReauth)
            await PublishAsync(ctx, ct);

        if (token is null)
            return null;

        var premium = row.Details.Any(d => d.key == ConnectionDetailKeys.SpotifyPremium && d.value == "true");

        return new ListenerGrant(token, premium, token.HasScope("user-modify-playback-state"));
    }

    public async Task<TokenUpkeep> UpkeepTokenAsync(ConnectionProvider provider, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserConnections.FirstOrDefaultAsync(x => x.UserId == UserId && x.Provider == provider, ct);

        if (row is null || row.SealedTokens is null || row.Status != ConnectionStatus.ACTIVE || providers.Get(provider) is not { } adapter)
            return TokenUpkeep.Untouched;

        var (_, upkeep) = await OpenTokenAsync(ctx, row, adapter, ct);

        if (upkeep is TokenUpkeep.NeedsReauth)
            await PublishAsync(ctx, ct);

        return upkeep;
    }

    /// <summary>
    /// The one place a sealed blob is opened. Refreshes when the token is inside the lead window,
    /// re-seals when the blob is under a retired key, and turns a gone grant into
    /// <c>NEEDS_REAUTH</c>. A transport failure on the refresh hands back the token as it is: it may
    /// still work, and the next pass tries again.
    /// </summary>
    private async Task<(ProviderToken? Token, TokenUpkeep Upkeep)> OpenTokenAsync(ApplicationDbContext ctx, UserConnectionEntity row,
        IConnectionProvider adapter, CancellationToken ct)
    {
        if (row.SealedTokens is null)
            return (null, TokenUpkeep.Untouched);

        if (!sealer.TryUnseal(row.SealedTokens, row.Id, row.Provider, out var token, out var staleKey) || token is null)
        {
            await MarkNeedsReauthAsync(ctx, row, "the stored token could not be opened", ct);
            return (null, TokenUpkeep.NeedsReauth);
        }

        var now = DateTimeOffset.UtcNow;

        if (token.ExpiresWithin(options.Value.TokenRefreshLead, now))
        {
            ProviderToken? fresh;

            try
            {
                fresh = await adapter.RefreshAsync(token, ct);
            }
            catch (Exception e) when (e is ProviderCallException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(e, "Refreshing the {Provider} token for {UserId} failed; keeping the current one", row.Provider, UserId);
                return (token, TokenUpkeep.Failed);
            }

            if (fresh is null)
            {
                await MarkNeedsReauthAsync(ctx, row, "the provider revoked the grant", ct);
                return (null, TokenUpkeep.NeedsReauth);
            }

            await StoreTokenAsync(ctx, row, fresh, ct);
            return (fresh, TokenUpkeep.Refreshed);
        }

        if (staleKey)
        {
            await StoreTokenAsync(ctx, row, token, ct);
            return (token, TokenUpkeep.Resealed);
        }

        return (token, TokenUpkeep.Untouched);
    }

    private async Task StoreTokenAsync(ApplicationDbContext ctx, UserConnectionEntity row, ProviderToken token, CancellationToken ct)
    {
        row.SealedTokens         = sealer.Seal(token, row.Id, row.Provider);
        row.TokenKeyVersion      = sealer.CurrentVersion;
        row.AccessTokenExpiresAt = token.ExpiresAt;
        row.Scopes               = Cap(token.Scopes, 1024) ?? row.Scopes;
        row.UpdatedAt            = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync(ct);
    }

    private async Task MarkNeedsReauthAsync(ApplicationDbContext ctx, UserConnectionEntity row, string reason, CancellationToken ct)
    {
        var wasActive = row.Status == ConnectionStatus.ACTIVE;

        row.Status               = ConnectionStatus.NEEDS_REAUTH;
        row.SealedTokens         = null;
        row.TokenKeyVersion      = 0;
        row.AccessTokenExpiresAt = null;
        row.LastError            = Cap(reason, 512);
        row.LastErrorAt          = DateTimeOffset.UtcNow;
        row.UpdatedAt            = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync(ct);

        logger.LogInformation("{Provider} connection of {UserId} needs re-authentication: {Reason}", row.Provider, UserId, reason);

        // Nothing for a poller to do until the person links again, which marks it back.
        await MarkStatusProviderAsync(row.Provider, shown: false);

        if (!wasActive)
            return;

        try
        {
            await notifications.CreateAsync(UserId, SystemNotificationType.ConnectionNeedsReauth, row.Id,
                $"Reconnect your {ConnectionProviders.DisplayName(row.Provider)} account", null, ct: ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not notify {UserId} about the {Provider} re-authentication", UserId, row.Provider);
        }
    }

    private async Task RevokeQuietlyAsync(UserConnectionEntity row, CancellationToken ct)
    {
        if (row.SealedTokens is null || providers.Get(row.Provider) is not { } adapter)
            return;

        if (!sealer.TryUnseal(row.SealedTokens, row.Id, row.Provider, out var token, out _) || token is null)
            return;

        try
        {
            await adapter.RevokeAsync(token, ct);
        }
        catch (Exception e) when (e is ProviderCallException or HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(e, "Revoking the {Provider} token for {UserId} did not go through; the row goes regardless", row.Provider, UserId);
        }
    }

    // ── side effects ─────────────────────────────────────────────────────────────────────────

    private async Task AfterLinkAsync(UserConnectionEntity row, CancellationToken ct)
    {
        if (row.Provider != ConnectionProvider.GITHUB)
            return;

        try
        {
            await GrainFactory.GetGrain<IConnectionTrophiesGrain>(Guid.Empty).CheckGitHubContributorAsync(UserId, row.ExternalId, ct);
        }
        catch (Exception e)
        {
            // The link stands; the coin is checked again on the next refresh.
            logger.LogWarning(e, "The contributor check for {UserId} failed", UserId);
        }
    }

    private async Task PublishAsync(ApplicationDbContext ctx, CancellationToken ct)
    {
        try
        {
            var mine = (await RowsAsync(ctx, ct)).Select(ToDto).ToList();
            await appHubServer.ForUser(new UserConnectionsUpdated(UserId, new IonArray<UserConnection>(mine)), UserId, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "UserConnectionsUpdated for {UserId} was not delivered", UserId);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private Task<List<UserConnectionEntity>> RowsAsync(ApplicationDbContext ctx, CancellationToken ct)
        => ctx.UserConnections.AsNoTracking()
           .Where(x => x.UserId == UserId)
           .OrderBy(x => x.Provider)
           .ToListAsync(ct);

    private static UserConnection ToDto(UserConnectionEntity r)
        => new(r.Provider, r.ExternalId, r.ExternalName, r.ExternalUrl, r.AvatarUrl, verified: true, r.Status,
            new ConnectionOptions(r.DisplayOnProfile, r.ShowDetails, r.DisplayAsStatus, r.AllowListenAlong),
            new IonArray<ConnectionDetail>(r.Details), r.CreatedAt.UtcDateTime, r.DetailsRefreshedAt?.UtcDateTime);

    private static ProfileConnection ToProfile(UserConnectionEntity r)
        => new(r.Provider, r.ExternalName, r.ExternalUrl, verified: true,
            r.ShowDetails ? new IonArray<ConnectionDetail>(r.Details) : IonArray<ConnectionDetail>.Empty);

    private static string? Cap(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];
}
