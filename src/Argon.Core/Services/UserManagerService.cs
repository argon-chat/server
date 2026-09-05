namespace Argon.Services;

using Features.Jwt;

public class UserManagerService(ILogger<UserManagerService> logger, IServiceProvider provider)
{
    // The refresh token carries a session id of our choosing so it can be revoked on its own.
    // Minted here rather than taken from the request: the caller's ArgonSecure cookie is the
    // caller's to write, and a revocation key it controls is not a revocation key.
    public Task<SuccessAuthorize> GenerateJwt(Guid id, string machineId, string[] scopes, string? deviceThumbprint = null)
        => GenerateJwt(id, machineId, scopes, ArgonId.New(), deviceThumbprint);

    /// <summary>
    /// Mints a session under a session id the caller already knows.
    /// </summary>
    /// <remarks>
    /// <para>For the one case where the server writes the device cookie itself rather than reading one
    /// the client wrote: the <c>scid</c> in that cookie and the <c>sid</c> inside the refresh token can
    /// then be the same value. That matters at sign-out, because the two are checked by different
    /// code against the same tombstone — the Ion interceptor keys on the cookie's session id, and the
    /// refresh path keys on the claim — and one tombstone can only end both if they agree. Where the
    /// client writes the cookie they cannot be made to agree, which is why this is an overload rather
    /// than the rule.</para>
    ///
    /// <para><paramref name="deviceThumbprint"/> binds the refresh token to a hardware key the caller
    /// has just proven possession of: every later refresh must present a fresh proof from that same
    /// key, so the token is worthless copied to another machine. Null — no proof was offered, or the
    /// machine has no key — leaves the token bound to the machine id alone, which is what every
    /// session looked like before hardware keys existed.</para>
    /// </remarks>
    public async Task<SuccessAuthorize> GenerateJwt(Guid id, string machineId, string[] scopes, Guid sessionId, string? deviceThumbprint = null)
    {
        await using var scope   = provider.CreateAsyncScope();
        var             jwt     = scope.ServiceProvider.GetRequiredService<ClassicJwtFlow>();
        var             access  = jwt.GenerateAccessToken(id, machineId, scopes);
        var             refresh = jwt.GenerateRefreshToken(id, machineId, scopes, sessionId, deviceThumbprint);

        if (deviceThumbprint is not null)
            logger.LogInformation("Issued a device-bound session {SessionId} for {UserId}", sessionId, id);

        return new SuccessAuthorize(access, refresh);
    }

    public async Task<SuccessAuthorize> GenerateJwt(Guid id, string[] scopes)
    {
        await using var scope   = provider.CreateAsyncScope();
        var             jwt     = scope.ServiceProvider.GetRequiredService<ClassicJwtFlow>();
        var             access  = jwt.GenerateAccessToken(id, scopes);
        return new SuccessAuthorize(access, null);
    }
}
