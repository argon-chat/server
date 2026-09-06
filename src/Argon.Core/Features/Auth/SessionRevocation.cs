namespace Argon.Features.Auth;

using Argon.Services;
using Microsoft.Extensions.Caching.Hybrid;

/// <summary>
/// The facts shared between the grain that ends a session and the paths that have to stop honouring
/// it: where the revocation lives, and for how long.
/// </summary>
/// <remarks>
/// <para>Access tokens are short-lived, so dropping a session's transport and letting its token
/// lapse would eventually be enough — except that the refresh token issued alongside it is stateless
/// and long-dated, and <c>GetMyAuthorization</c> will happily mint a fresh access token from it
/// until something stops it. Ending a session therefore has to leave something behind.</para>
///
/// <para>Two shapes, because one cannot do both jobs. <see cref="RevokedKey"/> is a per-user set of
/// revoked session ids — targeted, "end this device". <see cref="FloorKey"/> is a per-user issued-at
/// watermark that ends every token older than the moment it was written, which is what a password
/// change has to mean and the only handle on refresh tokens minted before the <c>sid</c> claim
/// existed.</para>
///
/// <para><b>Both are keyed per user, never per session.</b> A key per revoked session, held for the
/// refresh token's lifetime, grows without bound and never expires in practice — the store would
/// accumulate one entry for every device anyone has ever signed out, forever. A set costs one key
/// per user who has ever revoked anything, and its members are 36 bytes each; a user who has ended
/// a thousand sessions still costs tens of kilobytes.</para>
///
/// <para>Both are matched against claims <em>inside</em> the refresh token. The session id the rest
/// of the pipeline uses arrives in the <c>ArgonSecure</c> cookie, which the caller writes, so a
/// revocation matched against that is sidestepped by not sending it; the signed <c>sid</c> cannot
/// be. That is why the refresh path checks for itself rather than trusting the interceptor.</para>
///
/// <para><b>The identity model, in full, because every gate in the product depends on getting it
/// right.</b> A session has <em>two</em> ids and they live in different id spaces:</para>
///
/// <list type="bullet">
/// <item><description><b>The presence sid</b> — <c>HttpContextExtensions.GetSessionId()</c>: the
/// <c>scid</c> field of the <c>ArgonSecure</c> cookie, or the <c>Sec-Ref</c>/<c>X-Ctt</c> header.
/// <b>The caller writes it.</b> It names a row on the devices screen, it keys the presence records
/// and the session grain, and an installed client mints a fresh one on every launch. It is a
/// <em>label</em>, not a credential, and a gate that keys on it alone is escaped by sending a
/// different one.</description></item>
/// <item><description><b>The credential sid</b> — the <c>sid</c> claim <c>ClassicJwtFlow</c> writes
/// into the refresh token (and, since the same claim now rides the access token minted from it,
/// into every request that presents one). <b>The server mints it</b> and it is inside a signature,
/// so a caller can neither choose it nor omit it while still being served.</description></item>
/// </list>
///
/// <para>So the rule is: <b>a revocation is written under both ids and every gate tests both.</b>
/// <see cref="CredentialsKey"/> is the bridge that makes the first half possible —
/// <c>SecurityGrain.EndSessionAsync</c> tombstones the presence sid the user pressed the button on
/// <em>and</em> every credential sid recorded against it. The second half is why the hub ticket
/// carries <c>csid</c> claims beside its <c>sid</c>: without them a signed-out device escaped simply
/// by generating a new <c>scid</c> before reconnecting, since the only id the hub could see was the
/// one the caller had just chosen.</para>
///
/// <para>And because neither id can reach a credential that was never registered under either,
/// <see cref="FloorKey"/> is the backstop: <em>anything</em> issued at or before the watermark is
/// dead, whatever it calls itself. The refresh token and the hub ticket both carry an <c>iat</c>
/// for exactly that comparison, and a credential that carries none is read as older than any floor
/// — see <see cref="IsBelowFloor"/>.</para>
/// </remarks>
public static class SessionRevocation
{
    /// <summary>
    /// Where the request pipeline leaves the credential session id it read off the caller's access
    /// token, for the handful of places downstream that need the unforgeable half of the identity.
    /// </summary>
    /// <remarks>
    /// A property bag entry rather than a field on <c>ArgonRequestContextData</c>: the context is
    /// also built from an ion ticket and from a console token, neither of which has an access token
    /// to read, and a required field would make those two lie about having one. Absent means "this
    /// path could not establish it", which every reader has to tolerate anyway — see
    /// <c>EventBusImpl.PickTicket</c>, which unions it with <see cref="CredentialsKey"/>.
    /// </remarks>
    public const string CredentialSessionProperty = "csid";

    /// <summary>
    /// The hub-ticket claim carrying one server-minted credential session id.
    /// </summary>
    /// <remarks>
    /// Repeated, not joined: a ticket carries one claim per credential the device is known to hold,
    /// and <c>AppHub</c> tests every one of them against the tombstone set. <c>EventBusImpl.PickTicket</c>
    /// is the only writer.
    /// </remarks>
    public const string CredentialTicketClaim = "csid";

    /// <summary>
    /// How long a refresh token is good for, and therefore how long a revocation of it must be kept.
    /// </summary>
    /// <remarks>
    /// One constant for both so they cannot drift: a window shorter than the token it suppresses
    /// lapses first and quietly un-revokes the session. Ten years is the lifetime the tokens are
    /// minted with today, and it is what makes revocation state long-lived at all — shortening it
    /// and rotating on use is what would let this expire naturally, and that is a decision about how
    /// long a login lasts rather than one this file can make.
    /// </remarks>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(365 * 10);

    /// <summary>Retention for both keys below. Must be at least <see cref="RefreshTokenLifetime"/>.</summary>
    public static TimeSpan Window => RefreshTokenLifetime;

    /// <summary>Set of session ids whose refresh tokens are no longer honoured, for one user.</summary>
    public static string RevokedKey(Guid userId) => $"session:revoked:{userId}";

    /// <summary>
    /// The key shape this replaced: one string per revoked session.
    /// </summary>
    /// <remarks>
    /// <para>Still read, and that is not tidiness — it is the difference between a safe deploy and a
    /// silent one. Every revocation written before the set existed lives under this shape, and a
    /// server that only reads the new one would quietly start honouring sessions their owners had
    /// already ended.</para>
    ///
    /// <para>Safe to delete once no entry written under the old scheme can still be inside a refresh
    /// token's lifetime — which, given how long those live, means "after the old tombstones have been
    /// expired deliberately", not "after a while".</para>
    /// </remarks>
    public static string LegacyRevokedKey(Guid userId, Guid sessionId) => $"session:revoked:{userId}:{sessionId}";

    /// <summary>Refresh tokens issued at or before the stored instant are dead, for one user.</summary>
    public static string FloorKey(Guid userId) => $"session:floor:{userId}";

    /// <summary>
    /// The credential session ids a presence session has been seen holding: the bridge between the id
    /// on the devices screen and the id inside the refresh token.
    /// </summary>
    /// <remarks>
    /// <para>Defect S7. The two are different id spaces and always were. The devices screen lists the
    /// <em>presence</em> sid — the <c>scid</c> the client's <c>ArgonSecure</c> cookie carries, which the
    /// desktop regenerates on every launch — while <c>UserManagerService.GenerateJwt</c> mints the
    /// refresh token's <c>sid</c> server-side out of the request's sight. So a tombstone written for a
    /// row on the screen could never be the tombstone <see cref="RevokedKey"/> is checked against on
    /// the refresh path, and "sign this device out" ended the device's presence while leaving it a
    /// ten-year credential to come back with. Pinned by
    /// <c>PresenceRevocationTests.Revoking_a_device_stops_its_refresh_token_from_minting</c>.</para>
    ///
    /// <para>The mapping is written, never chosen: only the two moments where the server holds both
    /// halves at once — minting a session (sign-in, QR approval) and refreshing one
    /// (<c>GetMyAuthorization</c>) — record it, and both mint the credential id themselves. A caller
    /// picking its own <c>scid</c> therefore cannot point somebody else's credential at its row; the
    /// worst it can do is arrange for its own credential to be revoked. That is why this is a set
    /// rather than a single value: every credential that has presented itself under a given presence
    /// id gets ended with it, so a second sign-in under the same <c>scid</c> cannot displace the first
    /// one's entry and survive the sign-out.</para>
    ///
    /// <para>Keyed per presence session rather than per user, which is the opposite of the rule above
    /// — and for the opposite reason. A tombstone has to outlive the credential it suppresses; this
    /// only has to outlive the <em>row</em>, and a row exists only while the presence key behind it is
    /// being refreshed. <see cref="CredentialMappingWindow"/> bounds it, so the store holds one small
    /// set per launch for a month instead of one per launch for a decade.</para>
    /// </remarks>
    public static string CredentialsKey(Guid userId, Guid presenceSessionId)
        => $"session:credentials:{userId}:{presenceSessionId}";

    /// <summary>
    /// How long the presence-to-credential mapping is kept, counted from the last time it was written.
    /// </summary>
    /// <remarks>
    /// It is re-armed on every refresh, and an access token is good for a week, so a client that is
    /// still running has rewritten this four times over before it could lapse. The margin is what the
    /// month buys: if it lapses anyway — a session that never refreshed, a device signed in before
    /// this existed — revocation falls back to exactly what it did before, ending presence and no
    /// more. That makes the mapping additive rather than a flag day.
    /// </remarks>
    public static TimeSpan CredentialMappingWindow => TimeSpan.FromDays(30);

    /// <summary>
    /// Records that <paramref name="credentialSessionId"/> is the refresh-token session the presence
    /// session <paramref name="presenceSessionId"/> is holding.
    /// </summary>
    /// <remarks>
    /// Best effort on purpose: this runs on the sign-in and refresh paths, and a store that will not
    /// take the note is not a reason to refuse a user their session. The cost of losing it is that a
    /// later sign-out of that row ends presence only, which is the behaviour that existed before S7
    /// was fixed.
    /// </remarks>
    public static async Task RememberCredentialSessionAsync(
        IArgonCacheDatabase cache,
        ILogger             logger,
        Guid                userId,
        Guid?               presenceSessionId,
        Guid                credentialSessionId,
        CancellationToken   ct = default)
    {
        // Guid.AllBitsSet is what a development host hands every caller that sends no session header,
        // and Guid.Empty is "nothing was carried". Neither names a device, so neither is a row anyone
        // can press a button on — writing under them would only pool unrelated credentials together.
        if (presenceSessionId is not { } presence || presence == Guid.Empty || presence == Guid.AllBitsSet)
            return;

        try
        {
            var key = CredentialsKey(userId, presence);

            await cache.SetAddAsync(key, credentialSessionId.ToString(), ct);

            // EXPIRE, not GETEX — see the note on the tombstone in SecurityGrain.EndSessionAsync (S5).
            await cache.UpdateStringExpirationAsync(key, CredentialMappingWindow, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not record the credential session for {UserId}", userId);
        }
    }

    /// <summary>The credential session ids recorded against one presence session, or nothing.</summary>
    public static async Task<string[]> CredentialSessionsAsync(
        IArgonCacheDatabase cache, Guid userId, Guid presenceSessionId, CancellationToken ct = default)
        => await cache.SetMembersAsync(CredentialsKey(userId, presenceSessionId), ct);

    /// <summary>
    /// Reads the stored watermark: everything this user was issued at or before it is dead.
    /// </summary>
    /// <remarks>
    /// One parser for every gate, so the hub, the refresh path and anything added later cannot
    /// disagree about what an unparseable value means. Null is "no floor was ever written" and is
    /// also what a corrupt value reads as — a watermark nobody can place in time cannot be used to
    /// end sessions, and refusing every request on it would turn one bad write into a total lockout.
    /// </remarks>
    public static DateTimeOffset? ParseFloor(string? raw)
        => !string.IsNullOrEmpty(raw) && long.TryParse(raw, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

    /// <summary>
    /// Whether a credential issued at <paramref name="issuedAt"/> is below the user's floor.
    /// </summary>
    /// <remarks>
    /// A missing <paramref name="issuedAt"/> is treated as older than any floor, exactly as
    /// <c>IdentityInteraction.IsRefreshRevokedAsync</c> treats a refresh token minted before the
    /// claim existed: a credential that cannot be placed in time cannot be shown to be newer than a
    /// sign-out-everywhere, and the person who wrote the floor asked for everything to stop.
    /// </remarks>
    public static bool IsBelowFloor(DateTimeOffset? floor, DateTimeOffset? issuedAt)
        => floor is { } watermark && (issuedAt is not { } when || when <= watermark);

    /// <summary>
    /// How long a revocation may take to be honoured on a path that reads it through a cache.
    /// </summary>
    /// <remarks>
    /// One entry shape for every such reader — <c>ArgonTransactionInterceptor</c>, <c>AppHub</c>, the
    /// hub's own connection sweep — so the Ion path and the realtime path cannot disagree about when
    /// a sign-out takes effect. Cached at all because the answer is "no" for essentially every call
    /// ever made, and a heartbeat every fifteen seconds per connection is not worth a Redis round
    /// trip each.
    /// </remarks>
    public static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration           = TimeSpan.FromSeconds(15),
        LocalCacheExpiration = TimeSpan.FromSeconds(5),
    };

    /// <summary>Everything one user's revocation state amounts to: the tombstoned ids, and the floor.</summary>
    /// <remarks>
    /// Read together because every gate needs both and they are two lookups rather than one — a
    /// caller holding this can decide about any number of connections belonging to that user without
    /// going back to the store, which is what makes a per-connection sweep affordable.
    /// </remarks>
    public readonly record struct RevocationState(string[] Revoked, DateTimeOffset? Floor)
    {
        /// <summary>Whether any of <paramref name="identities"/> is on the tombstone set.</summary>
        /// <remarks>
        /// Written as a loop rather than as <c>Any</c> because a lambda inside a struct may not
        /// capture <c>this</c>, and copying the array out to satisfy that would be a copy per call.
        /// </remarks>
        public bool Names(IReadOnlyList<Guid> identities)
        {
            foreach (var identity in identities)
            {
                if (Revoked.Contains(identity.ToString()))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a credential presenting these identities and this issue time is dead — the set and
        /// the floor together, and everything a reader that skips the legacy keys can decide.
        /// </summary>
        public bool Ends(IReadOnlyList<Guid> identities, DateTimeOffset? issuedAt)
            => Names(identities) || IsBelowFloor(Floor, issuedAt);
    }

    /// <summary>
    /// Reads one user's tombstone set and floor.
    /// </summary>
    /// <param name="cache">
    /// The shared cache, or null to read straight through. Null is for the paths where a
    /// fifteen-second-stale "not revoked" is the wrong answer — accepting a new connection, starting
    /// a session — because those happen once and a user watching for a sign-out is watching for them.
    /// </param>
    /// <remarks>
    /// The empty string rather than null for a missing floor, because a cache entry holding nothing
    /// is indistinguishable from a cache miss and would be re-read on every heartbeat of every
    /// connection.
    /// </remarks>
    public static async Task<RevocationState> ReadStateAsync(
        IArgonCacheDatabase store, HybridCache? cache, Guid userId, CancellationToken ct = default)
    {
        var revokedKey = RevokedKey(userId);
        var floorKey   = FloorKey(userId);

        // The whole set per user, not one entry per (user, session) pair: it is a handful of ids.
        var revoked = cache is null
            ? await store.SetMembersAsync(revokedKey, ct)
            : await cache.GetOrCreateAsync(
                revokedKey,
                async token => await store.SetMembersAsync(revokedKey, token),
                CacheOptions,
                cancellationToken: ct);

        var floor = cache is null
            ? await store.StringGetAsync(floorKey, ct) ?? ""
            : await cache.GetOrCreateAsync(
                floorKey,
                async token => await store.StringGetAsync(floorKey, token) ?? "",
                CacheOptions,
                cancellationToken: ct);

        return new RevocationState(revoked, ParseFloor(floor));
    }

    /// <summary>Whether a revocation written under the pre-set key shape names this identity.</summary>
    /// <inheritdoc cref="LegacyRevokedKey"/>
    public static async Task<bool> IsLegacyRevokedAsync(
        IArgonCacheDatabase store, HybridCache? cache, Guid userId, Guid identity, CancellationToken ct = default)
    {
        var key = LegacyRevokedKey(userId, identity);

        return cache is null
            ? await store.KeyExistsAsync(key, ct)
            : await cache.GetOrCreateAsync(
                key,
                async token => await store.KeyExistsAsync(key, token),
                CacheOptions,
                cancellationToken: ct);
    }

    /// <summary>
    /// The whole gate, in one call: is a credential presenting these identities still honoured?
    /// </summary>
    /// <remarks>
    /// <para><b>Three things are tested, not one.</b> The tombstone set, which is where both of a
    /// session's ids are written when it is ended; the pre-set key shape, still read because every
    /// revocation written before the set existed lives under it; and the floor, which is the only
    /// handle a password change or a sign-out-everywhere has. See the remarks on this class for why
    /// the presence sid alone can never be enough.</para>
    ///
    /// <para><paramref name="failClosed"/> is the whole of the policy difference between the callers.
    /// A call on a socket that is already up fails <em>open</em> on a store error, because refusing
    /// those during a Redis incident would sign the whole instance out — the trade
    /// <c>ArgonTransactionInterceptor</c> makes and the reason it makes it. Accepting a new
    /// connection fails <em>closed</em>: refusing one costs a client a retry and heals itself,
    /// whereas admitting it hands a revoked device a fresh socket, every space group it used to be
    /// on, and a re-created presence row — and an attacker can arrange the incident cheaply, since
    /// the same instance carries presence, the replay buffers and the rate limiters.</para>
    /// </remarks>
    public static async Task<bool> IsRevokedAsync(
        IArgonCacheDatabase store,
        HybridCache?        cache,
        Guid                userId,
        IReadOnlyList<Guid> identities,
        DateTimeOffset?     issuedAt,
        bool                failClosed,
        ILogger             logger,
        CancellationToken   ct = default)
    {
        try
        {
            var state = await ReadStateAsync(store, cache, userId, ct);

            if (state.Names(identities))
                return true;

            foreach (var identity in identities)
            {
                if (await IsLegacyRevokedAsync(store, cache, userId, identity, ct))
                    return true;
            }

            return IsBelowFloor(state.Floor, issuedAt);
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Could not check the revocation of session {SessionId} for user {UserId}; {Decision} the caller",
                identities.Count > 0 ? identities[0] : Guid.Empty, userId, failClosed ? "refusing" : "allowing");

            return failClosed;
        }
    }
}
