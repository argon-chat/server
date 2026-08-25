namespace Argon.Services;

using Features.Jwt;

public class UserManagerService(ILogger<UserManagerService> logger, IServiceProvider provider)
{
    public async Task<SuccessAuthorize> GenerateJwt(Guid id, string machineId, string[] scopes)
    {
        await using var scope   = provider.CreateAsyncScope();
        var             jwt     = scope.ServiceProvider.GetRequiredService<ClassicJwtFlow>();
        var             access  = jwt.GenerateAccessToken(id, machineId, scopes);

        // The refresh token carries a session id of our choosing so it can be revoked on its own.
        // Minted here rather than taken from the request: the caller's ArgonSecure cookie is the
        // caller's to write, and a revocation key it controls is not a revocation key.
        var refresh = jwt.GenerateRefreshToken(id, machineId, scopes, ArgonId.New());

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