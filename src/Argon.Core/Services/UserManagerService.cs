namespace Argon.Services;

using Features.Jwt;

public class UserManagerService(ILogger<UserManagerService> logger, IServiceProvider provider)
{
    public async Task<SuccessAuthorize> GenerateJwt(Guid id, string machineId, string[] scopes)
    {
        await using var scope   = provider.CreateAsyncScope();
        var             jwt     = scope.ServiceProvider.GetRequiredService<ClassicJwtFlow>();
        var             access  = jwt.GenerateAccessToken(id, machineId, scopes);
        var             refresh = jwt.GenerateRefreshToken(id, machineId, scopes);
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