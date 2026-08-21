namespace Argon.Features.Auth;

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
}
