namespace Argon.Features.Auth;

using Argon.Services;

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
/// </remarks>
public static class SessionRevocation
{
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
}
