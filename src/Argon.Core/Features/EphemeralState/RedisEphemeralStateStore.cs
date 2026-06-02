namespace Argon.Features.EphemeralState;

using Newtonsoft.Json;
using Services;

public sealed class RedisEphemeralStateStore(IArgonCacheDatabase cache) : IEphemeralStateStore
{
    private static string VoiceChannelKey(Guid channelId)
        => $"voice:channel:{channelId:N}";

    private static string VoiceSlotKey(Guid spaceId, Guid userId)
        => $"voice:slot:{spaceId:N}:{userId:N}";

    private static string VoiceSlotsPattern(Guid spaceId)
        => $"voice:slot:{spaceId:N}:*";

    private static string SessionRouteKey(string sessionId)
        => $"session:route:{sessionId}";

    private static string UserSessionsPattern(Guid userId)
        => $"session:user:{userId:N}:*";

    private static string UserSessionIndexKey(Guid userId, string sessionId)
        => $"session:user:{userId:N}:{sessionId}";

    // --- Voice Channel State ---

    public async Task<Dictionary<Guid, VoiceChannelMember>> GetVoiceChannelMembersAsync(
        Guid channelId, CancellationToken ct)
    {
        var json = await cache.StringGetAsync(VoiceChannelKey(channelId), ct);
        if (string.IsNullOrEmpty(json)) return new();
        return JsonConvert.DeserializeObject<Dictionary<Guid, VoiceChannelMember>>(json) ?? new();
    }

    public async Task SetVoiceChannelMemberAsync(
        Guid channelId, Guid userId, VoiceChannelMember member, CancellationToken ct)
    {
        var members = await GetVoiceChannelMembersAsync(channelId, ct);
        members[userId] = member;
        await cache.StringSetAsync(VoiceChannelKey(channelId), JsonConvert.SerializeObject(members), ct);
    }

    public async Task RemoveVoiceChannelMemberAsync(
        Guid channelId, Guid userId, CancellationToken ct)
    {
        var members = await GetVoiceChannelMembersAsync(channelId, ct);
        if (members.Remove(userId))
        {
            if (members.Count == 0)
                await cache.KeyDeleteAsync(VoiceChannelKey(channelId), ct);
            else
                await cache.StringSetAsync(VoiceChannelKey(channelId), JsonConvert.SerializeObject(members), ct);
        }
    }

    public Task RemoveAllVoiceChannelMembersAsync(Guid channelId, CancellationToken ct)
        => cache.KeyDeleteAsync(VoiceChannelKey(channelId), ct);

    // --- Voice Reverse Index ---

    public async Task<VoiceSlotEntry?> GetUserVoiceSlotAsync(Guid spaceId, Guid userId, CancellationToken ct)
    {
        var json = await cache.StringGetAsync(VoiceSlotKey(spaceId, userId), ct);
        if (string.IsNullOrEmpty(json)) return null;
        return JsonConvert.DeserializeObject<VoiceSlotEntry>(json);
    }

    public Task SetUserVoiceSlotAsync(Guid spaceId, Guid userId, VoiceSlotEntry slot, CancellationToken ct)
        => cache.StringSetAsync(VoiceSlotKey(spaceId, userId), JsonConvert.SerializeObject(slot), ct);

    public Task RemoveUserVoiceSlotAsync(Guid spaceId, Guid userId, CancellationToken ct)
        => cache.KeyDeleteAsync(VoiceSlotKey(spaceId, userId), ct);

    public async Task<Dictionary<Guid, VoiceSlotEntry>> GetAllVoiceSlotsAsync(Guid spaceId, CancellationToken ct)
    {
        var result = new Dictionary<Guid, VoiceSlotEntry>();
        var prefix = $"voice:slot:{spaceId:N}:";

        await foreach (var key in cache.ScanKeysAsync(VoiceSlotsPattern(spaceId), ct).WithCancellation(ct))
        {
            if (!Guid.TryParse(key[prefix.Length..], out var userId)) continue;
            var json = await cache.StringGetAsync(key, ct);
            if (string.IsNullOrEmpty(json)) continue;
            var slot = JsonConvert.DeserializeObject<VoiceSlotEntry>(json);
            if (slot is not null)
                result[userId] = slot;
        }

        return result;
    }

    // --- Session Routing ---

    public async Task SetSessionRouteAsync(string sessionId, SessionRoute route, TimeSpan ttl, CancellationToken ct)
    {
        var json = JsonConvert.SerializeObject(route);
        await cache.StringSetAsync(SessionRouteKey(sessionId), json, ttl, ct);
        await cache.StringSetAsync(UserSessionIndexKey(route.UserId, sessionId), json, ttl, ct);
    }

    public Task RefreshSessionRouteAsync(string sessionId, TimeSpan ttl, CancellationToken ct)
        => cache.UpdateStringExpirationAsync(SessionRouteKey(sessionId), ttl, ct);

    public async Task RemoveSessionRouteAsync(string sessionId, CancellationToken ct)
    {
        var json = await cache.StringGetAsync(SessionRouteKey(sessionId), ct);
        await cache.KeyDeleteAsync(SessionRouteKey(sessionId), ct);
        if (!string.IsNullOrEmpty(json))
        {
            var route = JsonConvert.DeserializeObject<SessionRoute>(json);
            if (route is not null)
                await cache.KeyDeleteAsync(UserSessionIndexKey(route.UserId, sessionId), ct);
        }
    }

    public async Task<SessionRoute?> GetSessionRouteAsync(string sessionId, CancellationToken ct)
    {
        var json = await cache.StringGetAsync(SessionRouteKey(sessionId), ct);
        if (string.IsNullOrEmpty(json)) return null;
        return JsonConvert.DeserializeObject<SessionRoute>(json);
    }

    public async Task<List<SessionRoute>> GetUserSessionRoutesAsync(Guid userId, CancellationToken ct)
    {
        var routes = new List<SessionRoute>();
        await foreach (var key in cache.ScanKeysAsync(UserSessionsPattern(userId), ct).WithCancellation(ct))
        {
            var json = await cache.StringGetAsync(key, ct);
            if (string.IsNullOrEmpty(json)) continue;
            var route = JsonConvert.DeserializeObject<SessionRoute>(json);
            if (route is not null)
                routes.Add(route);
        }
        return routes;
    }
}
