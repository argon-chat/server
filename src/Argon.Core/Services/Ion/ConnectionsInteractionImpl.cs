namespace Argon.Services.Ion;

using Argon.Core.Entities.Data;
using Argon.Features.Integrations.Connections;
using Argon.Features.Integrations.Connections.Spotify;
using ion.runtime;

/// <summary>
/// The first-party surface of linked accounts. Maps, checks the caller and the flags, and calls
/// grains; the adapters, the secrets and the rows are all on the silo.
/// </summary>
public sealed class ConnectionsInteractionImpl(
    ListenAlongDirectory listenAlong,
    ILogger<IConnectionsInteraction> logger) : IConnectionsInteraction
{
    private IConnectionHandshakeGrain Handshake => this.GetGrain<IConnectionHandshakeGrain>(Guid.Empty);
    private IUserConnectionsGrain     Mine      => this.GetGrain<IUserConnectionsGrain>(this.GetUserId());

    public async Task<IonArray<ConnectionProviderInfo>> GetProviders(CancellationToken ct = default)
    {
        var configured = await Handshake.GetProvidersAsync(ct);
        var allowed    = await AllowedAsync(configured.Select(p => p.provider), ct);

        return new IonArray<ConnectionProviderInfo>(configured.Where(p => allowed.Contains(p.provider)).ToList());
    }

    public async Task<IonArray<UserConnection>> GetMyConnections(CancellationToken ct = default)
        => new(await Mine.GetMineAsync(ct));

    public async Task<IBeginConnectResult> BeginConnect(ConnectionProvider provider, ConnectReturnKind returnTo, bool replace, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        if (!(await AllowedAsync([provider], ct)).Contains(provider))
            return new FailedBeginConnect(BeginConnectError.PROVIDER_DISABLED);

        try
        {
            var outcome = await Handshake.BeginAsync(this.GetUserId(), provider, returnTo, replace, ct);

            return outcome.Error == BeginConnectError.NONE
                ? new SuccessBeginConnect(outcome.Url!, outcome.HandshakeId, outcome.ExpiresAt.UtcDateTime)
                : new FailedBeginConnect(outcome.Error);
        }
        catch (Exception e)
        {
            logger.LogError(e, "BeginConnect {Provider} failed for {UserId}", provider, this.GetUserId());
            return new FailedBeginConnect(BeginConnectError.INTERNAL_ERROR);
        }
    }

    public Task<bool> Disconnect(ConnectionProvider provider, CancellationToken ct = default)
        => Mine.UnlinkAsync(provider, ct);

    public async Task<IUpdateConnectionResult> UpdateOptions(ConnectionProvider provider, IonPartial<ConnectionOptions> patch, CancellationToken ct = default)
    {
        var result = await Mine.UpdateOptionsAsync(provider, patch, ct);

        return result.IsSuccess
            ? new SuccessUpdateConnection(result.Value)
            : new FailedUpdateConnection(result.Error);
    }

    public async Task<IRefreshConnectionResult> RefreshConnection(ConnectionProvider provider, CancellationToken ct = default)
    {
        var result = await Mine.RefreshDetailsAsync(provider, force: false, ct);

        return result.IsSuccess
            ? new SuccessRefreshConnection(result.Value)
            : new FailedRefreshConnection(result.Error);
    }

    public async Task<IonArray<ProfileConnection>> GetUserConnections(Guid userId, CancellationToken ct = default)
    {
        var callerId = this.GetUserId();

        // The same standing a profile lookup needs: a bare user id is not enough to walk the directory.
        if (callerId != userId && !await this.GetGrain<IIdentityDirectoryGrain>(Guid.Empty).CanReachAsync(callerId, userId, ct))
            return IonArray<ProfileConnection>.Empty;

        return new IonArray<ProfileConnection>(await this.GetGrain<IUserConnectionsGrain>(userId).GetVisibleForAsync(callerId, ct));
    }

    public async Task<IListenAlongResult> JoinListenAlong(Guid hostUserId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Middle);

        try
        {
            var join = await this.GetGrain<IListenAlongGrain>(hostUserId).JoinAsync(this.GetUserId(), ct);

            return join.Error == ListenAlongError.NONE && join.State is { } state
                ? new SuccessListenAlong(state)
                : new FailedListenAlong(join.Error);
        }
        catch (Exception e)
        {
            logger.LogError(e, "JoinListenAlong with {Host} failed for {UserId}", hostUserId, this.GetUserId());
            return new FailedListenAlong(ListenAlongError.PROVIDER_ERROR);
        }
    }

    public async Task LeaveListenAlong(CancellationToken ct = default)
    {
        var userId = this.GetUserId();

        if (await listenAlong.GetHostAsync(userId, ct) is { } host)
            await this.GetGrain<IListenAlongGrain>(host).LeaveAsync(userId, ListenAlongEndReason.LEFT, ct);
    }

    public async Task<ListenAlongState?> GetListenAlongState(CancellationToken ct = default)
    {
        var userId = this.GetUserId();

        // As a listener first; failing that, as a host with a party of their own.
        if (await listenAlong.GetHostAsync(userId, ct) is { } host && await this.GetGrain<IListenAlongGrain>(host).GetStateAsync() is { } party)
            return party;

        return await this.GetGrain<IListenAlongGrain>(userId).GetStateAsync();
    }

    /// <summary>
    /// The providers the caller's flags allow. A provider whose flag is not defined is allowed —
    /// configuration decides whether it exists, the flag only holds one back (Spotify, until its
    /// quota is granted).
    /// </summary>
    private async Task<HashSet<ConnectionProvider>> AllowedAsync(IEnumerable<ConnectionProvider> providers, CancellationToken ct)
    {
        var candidates = providers.ToHashSet();

        if (candidates.Count == 0)
            return candidates;

        var flags = await this.GetGrain<IFeatureFlagGrain>(Guid.Empty).EvaluateAllAsync(
            FeatureFlagEvaluationContext.ForUser(this.GetUserId(), this.GetUserCountry(), this.GetClientId()), includeDisabled: true);

        candidates.RemoveWhere(p => flags.TryGetValue(ConnectionProviders.FlagOf(p), out var flag) && !flag.IsEnabled);

        return candidates;
    }
}
