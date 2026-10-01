namespace Argon.Grains.Interfaces;

using Argon.Features.Integrations.Connections;
using ion.runtime;

public sealed record ExistingConnection(string ExternalId, string Name, ConnectionStatus Status);

/// <summary>A completed handshake, ready to become a row.</summary>
public sealed record LinkRequest(
    ConnectionProvider Provider,
    ConnectionIdentity Identity,
    ProviderToken? Token,
    List<ConnectionDetail> Details,
    string Scopes,
    bool Replace);

public enum LinkOutcome
{
    Linked,

    /// <summary>This external identity vouches for another Argon account already.</summary>
    AlreadyLinkedElsewhere,

    /// <summary>The user has a different account of this provider linked and did not ask to replace it.</summary>
    ProviderAlreadyLinked,

    /// <summary>An operator suspended the user's connection of this provider; it is not re-linked over.</summary>
    Suspended
}

/// <summary>What a status poller (Spotify) may do on the user's behalf right now.</summary>
public sealed record ActivityGrant(ProviderToken Token, bool AllowListenAlong);

/// <summary>What a would-be listener brings to a listen-along: a token, and whether it can drive playback.</summary>
public sealed record ListenerGrant(ProviderToken Token, bool Premium, bool CanControlPlayback);

/// <summary>What one maintenance pass did to a row's token.</summary>
public enum TokenUpkeep
{
    Untouched,
    Refreshed,
    Resealed,
    NeedsReauth,
    Failed
}

/// <summary>
/// One user's linked accounts: the rows, their options and details, and the only place their
/// tokens are opened.
/// </summary>
/// <remarks>
/// A single activation per user, because a link racing an unlink is how a row ends up holding the
/// tokens of a connection the person just removed. Every write here ends in a
/// <c>UserConnectionsUpdated</c> to the owner's sessions. Tokens leave only as a
/// <see cref="ProviderToken"/> to grains on the same role; no Ion result carries one.
/// </remarks>
[Alias("Argon.Grains.Interfaces.IUserConnectionsGrain")]
public interface IUserConnectionsGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetMineAsync))]
    Task<List<UserConnection>> GetMineAsync(CancellationToken ct = default);

    /// <summary>What <paramref name="viewerId"/> may see on the card: options, privacy rule and status applied.</summary>
    [Alias(nameof(GetVisibleForAsync))]
    Task<List<ProfileConnection>> GetVisibleForAsync(Guid viewerId, CancellationToken ct = default);

    [Alias(nameof(GetExistingAsync))]
    Task<ExistingConnection?> GetExistingAsync(ConnectionProvider provider, CancellationToken ct = default);

    [Alias(nameof(LinkAsync))]
    Task<LinkOutcome> LinkAsync(LinkRequest request, CancellationToken ct = default);

    /// <summary>Revokes at the provider where possible and deletes the row. False when there was none.</summary>
    [Alias(nameof(UnlinkAsync))]
    Task<bool> UnlinkAsync(ConnectionProvider provider, CancellationToken ct = default);

    [Alias(nameof(UpdateOptionsAsync))]
    Task<Either<UserConnection, ConnectionError>> UpdateOptionsAsync(ConnectionProvider provider, IonPartial<ConnectionOptions> patch, CancellationToken ct = default);

    /// <summary>Reads the account again at the provider. <paramref name="force"/> skips the manual cooldown (maintenance).</summary>
    [Alias(nameof(RefreshDetailsAsync))]
    Task<Either<UserConnection, ConnectionError>> RefreshDetailsAsync(ConnectionProvider provider, bool force, CancellationToken ct = default);

    /// <summary>A live token for the provider, refreshed if it was about to expire; null when there is none to be had.</summary>
    [Alias(nameof(GetTokenAsync))]
    Task<ProviderToken?> GetTokenAsync(ConnectionProvider provider, CancellationToken ct = default);

    /// <summary>
    /// The token and the listen-along option, when the connection is active and shown as status;
    /// null otherwise. What the status poller asks on every tick.
    /// </summary>
    [Alias(nameof(GetActivityGrantAsync))]
    Task<ActivityGrant?> GetActivityGrantAsync(ConnectionProvider provider, CancellationToken ct = default);

    /// <summary>
    /// The Spotify token with what listen-along needs to know about it; null when Spotify is not
    /// linked and active. Showing the connection as status is not required to listen along.
    /// </summary>
    [Alias(nameof(GetListenerGrantAsync))]
    Task<ListenerGrant?> GetListenerGrantAsync(CancellationToken ct = default);

    /// <summary>The maintenance tick's per-row work: refresh before expiry, re-seal under the current key.</summary>
    [Alias(nameof(UpkeepTokenAsync))]
    Task<TokenUpkeep> UpkeepTokenAsync(ConnectionProvider provider, CancellationToken ct = default);

    /// <summary>Account deletion: revoke what can be revoked and drop every row.</summary>
    [Alias(nameof(PurgeAsync))]
    Task PurgeAsync(CancellationToken ct = default);

    /// <summary>The operator console's view of the rows.</summary>
    [Alias(nameof(GetForOperatorAsync))]
    Task<List<ConsoleContracts.AdminUserConnection>> GetForOperatorAsync(CancellationToken ct = default);

    /// <summary>
    /// An operator's suspension: the connection is hidden and its poller stopped, the row stays so
    /// the same external account is not linked over it. Lifting it puts the row back to active if
    /// it still has a token, to needs-reauth if it does not. False when there is no such row.
    /// </summary>
    [Alias(nameof(SetSuspendedAsync))]
    Task<bool> SetSuspendedAsync(ConnectionProvider provider, bool suspended, CancellationToken ct = default);
}
