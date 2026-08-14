namespace Argon.Features.Auth;

/// <summary>
/// The one fact that has to be shared between the grain that ends a session and the interceptor that
/// has to stop honouring it: where the tombstone lives, and for how long.
/// </summary>
/// <remarks>
/// <para>Access tokens are short-lived, so dropping the session's transport and letting the token
/// lapse would eventually be enough — except that the refresh token issued alongside it is stateless
/// and dated ten years out, and <c>GetMyAuthorization</c> will happily mint a fresh access token from
/// it forever. Ending a session therefore has to leave something behind that the next request trips
/// over, and this is it.</para>
///
/// <para><see cref="Window"/> is a compromise, and an honest one: a tombstone is a per-session key
/// held on behalf of a user who may never come back, so it cannot be kept for the refresh token's
/// full lifetime without the tombstones outliving everything else in the store. Thirty days covers
/// the case this feature exists for — a session the user does not recognise, ended now — and closing
/// the remainder properly needs a token version on the user row that the refresh path checks, which
/// is a change to how every token in the platform is validated and not to this screen.</para>
/// </remarks>
public static class SessionRevocation
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public static string Key(Guid userId, Guid sessionId) => $"session:revoked:{userId}:{sessionId}";
}
