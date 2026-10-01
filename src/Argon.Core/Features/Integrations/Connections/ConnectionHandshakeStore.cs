namespace Argon.Features.Integrations.Connections;

using Argon.Services;

/// <summary>
/// The handshake's short-lived state, in Redis and nowhere else: nothing reaches the database until
/// the provider has said who the person is.
/// </summary>
/// <remarks>
/// <para>Two records. A <see cref="Started"/> handshake, keyed by its state, consumed exactly once
/// when the provider redirects back — the consume is <c>SET … GET</c> with a one-second tombstone,
/// so a replayed callback finds the tombstone and nothing else. A <see cref="Parked"/> result, keyed
/// by handshake id, holds the completed exchange while the browser proves who it is; the token in
/// it is sealed under the handshake id, so a Redis dump is not a token dump.</para>
///
/// <para>Two counters. The begin limiter and the refresh cooldown live here as well because the
/// grains that enforce them are stateless workers with many activations, and a per-activation
/// window would be no limit at all.</para>
/// </remarks>
public sealed class ConnectionHandshakeStore(IArgonCacheDatabase cache, TokenSealer sealer, IOptions<ConnectionsOptions> options)
{
    private const string Consumed = "\u0000consumed";

    private static readonly TimeSpan Tombstone = TimeSpan.FromSeconds(1);

    public sealed record Started(
        Guid HandshakeId,
        Guid UserId,
        ConnectionProvider Provider,
        string State,
        string? PkceVerifier,
        string RedirectUri,
        ConnectReturnKind ReturnTo,
        bool Replace,
        DateTimeOffset CreatedAt);

    public sealed record Parked(
        Guid HandshakeId,
        Guid UserId,
        ConnectionProvider Provider,
        ConnectionIdentity Identity,
        string? SealedToken,
        List<ConnectionDetail> Details,
        string Scopes,
        ConnectReturnKind ReturnTo,
        bool Replace,
        DateTimeOffset CreatedAt);

    private TimeSpan Ttl => options.Value.HandshakeTtl;

    private static string StartedKey(string state)   => $"conn:handshake:{state}";
    private static string ParkedKey(Guid handshakeId) => $"conn:pending:{handshakeId:N}";

    public Task PutStartedAsync(Started started, CancellationToken ct = default)
        => cache.StringSetAsync(StartedKey(started.State), JsonConvert.SerializeObject(started), Ttl, ct);

    /// <summary>The handshake behind a state, once. The second caller gets null.</summary>
    public async Task<Started?> ConsumeStartedAsync(string state, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(state) || state.Length > 128)
            return null;

        var previous = await cache.StringSetAndGetPreviousAsync(StartedKey(state), Consumed, Tombstone, ct);

        return Read<Started>(previous);
    }

    public Task ParkAsync(Guid handshakeId, Guid userId, ConnectionProvider provider, HandshakeResult result,
        ConnectReturnKind returnTo, bool replace, CancellationToken ct = default)
    {
        var sealedToken = result.Token is { } token
            ? Convert.ToBase64String(sealer.Seal(token, handshakeId, provider))
            : null;

        var parked = new Parked(handshakeId, userId, provider, result.Identity!, sealedToken, result.Details.ToList(),
            result.Scopes, returnTo, replace, DateTimeOffset.UtcNow);

        return cache.StringSetAsync(ParkedKey(handshakeId), JsonConvert.SerializeObject(parked), Ttl, ct);
    }

    /// <summary>The parked result without consuming it — for the page that asks the browser to sign in.</summary>
    public async Task<Parked?> PeekParkedAsync(Guid handshakeId, CancellationToken ct = default)
        => Read<Parked>(await cache.StringGetAsync(ParkedKey(handshakeId), ct));

    public async Task<Parked?> ConsumeParkedAsync(Guid handshakeId, CancellationToken ct = default)
        => Read<Parked>(await cache.StringSetAndGetPreviousAsync(ParkedKey(handshakeId), Consumed, Tombstone, ct));

    public Task DropParkedAsync(Guid handshakeId, CancellationToken ct = default)
        => cache.KeyDeleteAsync(ParkedKey(handshakeId), ct);

    public ProviderToken? OpenParkedToken(Parked parked)
    {
        if (parked.SealedToken is null)
            return null;

        return sealer.TryUnseal(Convert.FromBase64String(parked.SealedToken), parked.HandshakeId, parked.Provider, out var token, out _)
            ? token
            : null;
    }

    /// <summary>One more <c>BeginConnect</c> for this user inside the current minute, if the budget allows.</summary>
    public async Task<bool> TryAcquireBeginAsync(Guid userId, CancellationToken ct = default)
    {
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var key    = $"conn:begin:{userId:N}:{minute}";
        var count  = await cache.StringIncrementAsync(key, ct);

        if (count == 1)
            await cache.UpdateStringExpirationAsync(key, TimeSpan.FromMinutes(2), ct);

        return count <= options.Value.BeginConnectPerMinute;
    }

    /// <summary>Whether a manual details refresh may run now; arms the cooldown when it may.</summary>
    public async Task<bool> TryAcquireRefreshAsync(Guid userId, ConnectionProvider provider, CancellationToken ct = default)
    {
        var cooldown = options.Value.RefreshCooldown;

        if (cooldown <= TimeSpan.Zero)
            return true;

        var previous = await cache.StringSetAndGetPreviousAsync($"conn:refresh:{userId:N}:{(int)provider}", "1", cooldown, ct);

        return previous is null;
    }

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json) || json == Consumed)
            return null;

        try
        {
            return JsonConvert.DeserializeObject<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
