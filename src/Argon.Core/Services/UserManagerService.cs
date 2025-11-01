namespace Argon.Services;

using Features.Jwt;

public class UserManagerService(ILogger<UserManagerService> logger, IServiceProvider provider)
{
    public async Task<string> GenerateJwt(Guid id, string machineId, string[] scopes)
    {
        await using var scope = provider.CreateAsyncScope();
        var             jwt   = scope.ServiceProvider.GetRequiredService<ClassicJwtFlow>();
        return jwt.GenerateAccessToken(id, machineId, scopes);
    }
}