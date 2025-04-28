namespace Argon.Features.Logic;

using Newtonsoft.Json;
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
    Task                         HeartbeatAsync(Guid userId, Guid sessionId, CancellationToken ct = default);
    Task<bool>                   IsUserOnlineAsync(Guid userId, CancellationToken ct = default);
    Task<Dictionary<Guid, bool>> AreUsersOnlineAsync(IEnumerable<Guid> userIds, CancellationToken ct = default);

    Task<List<Guid>> GetActiveSessionIdsAsync(Guid userId, CancellationToken ct = default);

    Task BroadcastActivityPresence(UserActivityPresence presence, Guid userId, Guid sessionId);

    Task<Dictionary<Guid, UserActivityPresence>> BatchGetUsersActivityPresence(List<Guid> userIds);
    Task<UserActivityPresence?>                  GetUsersActivityPresence(Guid userId);
    Task                                         RemoveActivityPresence(Guid userId);
}

public class UserPresenceService(IArgonCacheDatabase cache) : IUserPresenceService
{
    public static readonly TimeSpan DefaultTTL = TimeSpan.FromSeconds(30);

    private static string SessionKey(Guid userId, Guid sessionId)
        => $"presence:user:{userId}:session:{sessionId}";

    private static string SessionKeyPrefix(Guid userId)
        => $"presence:user:{userId}:session:*";

    private static string SessionPresenceKey(Guid userId)
        => $"activity:user:{userId}:session:broadcast";

    private static string SessionPresenceKeyPrefix(Guid userId)
        => $"activity:user:{userId}:session:broadcast";

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
        return cache.StringSetAsync(key, "1", DefaultTTL, ct);
    }

    public async Task<bool> IsUserOnlineAsync(Guid userId, CancellationToken ct = default)
    {
        await foreach (var _ in cache.ScanKeysAsync(SessionKeyPrefix(userId)).WithCancellation(ct))
            return true;

        return false;
    }

    public async Task<Dictionary<Guid, bool>> AreUsersOnlineAsync(IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var tasks = userIds.ToDictionary(
            userId => userId,
            userId => Task.Run(async () => {
                await foreach (var _ in cache.ScanKeysAsync(SessionKeyPrefix(userId)).WithCancellation(ct))
                    return true;
                return false;
            }, ct)
        );

        var results = await Task.WhenAll(tasks.Values);

        return tasks.Keys.Zip(results, (key, result) => new { key, result })
           .ToDictionary(x => x.key, x => x.result);
    }

    public async Task<List<Guid>> GetActiveSessionIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var sessionIds = new List<Guid>();
        var prefix     = $"presence:user:{userId}:session:";

        await foreach (var key in cache.ScanKeysAsync(SessionKeyPrefix(userId)).WithCancellation(ct))
        {
            if (key.StartsWith(prefix))
                sessionIds.Add(Guid.Parse(key[prefix.Length..]));
        }

        return sessionIds;
    }

    public async Task BroadcastActivityPresence(UserActivityPresence presence, Guid userId, Guid sessionId)
        => await cache.StringSetAsync(SessionPresenceKey(userId), JsonConvert.SerializeObject(presence));

    public async Task<Dictionary<Guid, UserActivityPresence>> BatchGetUsersActivityPresence(List<Guid> userIds)
    {
        var keys = await Task.WhenAll(userIds.Select(async x => (await cache.StringGetAsync(SessionPresenceKeyPrefix(x)), x)));
        var dict = new Dictionary<Guid, UserActivityPresence>();
        foreach (var (presence, userId) in keys.Where(x => !string.IsNullOrEmpty(x.Item1)))
            dict.Add(userId, JsonConvert.DeserializeObject<UserActivityPresence>(presence!)!);

        return dict;
    }

    public async Task<UserActivityPresence?> GetUsersActivityPresence(Guid userId)
    {
        var presence = await cache.StringGetAsync(SessionPresenceKeyPrefix(userId));
        return string.IsNullOrEmpty(presence) ? null : JsonConvert.DeserializeObject<UserActivityPresence>(presence);
    }

    public Task RemoveActivityPresence(Guid userId)
        => cache.KeyDeleteAsync(SessionPresenceKeyPrefix(userId));
}