namespace Argon.Features.Logic;

using Services;

public static class UserPresenceFeature
{
    public static IServiceCollection AddUserPresenceFeature(this IHostApplicationBuilder hostBuilder)
    {
        hostBuilder.Services.AddSingleton<IUserPresenceService, UserPresenceService>();
        return hostBuilder.Services;
    }
}

public interface IUserPresenceService
{
    Task HeartbeatAsync(Guid userId, Guid sessionId, CancellationToken ct = default);
    Task<bool> IsUserOnlineAsync(Guid userId, CancellationToken ct = default);
    Task<List<Guid>> GetActiveSessionIdsAsync(Guid userId, CancellationToken ct = default);
}

public class UserPresenceService(IArgonCacheDatabase cache) : IUserPresenceService
{
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(30);

    private static string SessionKey(Guid userId, Guid sessionId)
        => $"presence:user:{userId}:session:{sessionId}";
    private static string SessionKeyPrefix(Guid userId)
        => $"presence:user:{userId}:session:*";

    public Task SetSessionOnlineAsync(Guid userId, Guid sessionId, TimeSpan ttl, CancellationToken ct = default)
    {
        var key = SessionKey(userId, sessionId);
        return cache.StringSetAsync(key, "1", ttl, ct);
    }

    public Task RemoveSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var key = SessionKey(userId, sessionId);
        return cache.KeyDeleteAsync(key, ct);
    }

    public Task HeartbeatAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var key = SessionKey(userId, sessionId);
        return cache.StringSetAsync(key, "1", _ttl, ct);
    }

    public async Task<bool> IsUserOnlineAsync(Guid userId, CancellationToken ct = default)
    {
        await foreach (var _ in cache.ScanKeysAsync(SessionKeyPrefix(userId)).WithCancellation(ct))
            return true;

        return false;
    }

    public async Task<List<Guid>> GetActiveSessionIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var sessionIds = new List<Guid>();
        var prefix = $"presence:user:{userId}:session:";

        await foreach (var key in cache.ScanKeysAsync(SessionKeyPrefix(userId)).WithCancellation(ct))
        {
            if (key.StartsWith(prefix))
                sessionIds.Add(Guid.Parse(key[prefix.Length..]));
        }

        return sessionIds;
    }
}