namespace Argon.Core.Features.CoreLogic.Passkeys;

using Api.Features.CoreLogic.Otp;
using Argon.Core.Features.Logic;
using Microsoft.Extensions.Caching.Distributed;

public interface IPendingPasskeyStore
{
    /// <summary>
    /// Creates a pending passkey and stores it in cache with a challenge.
    /// </summary>
    Task<(Guid passkeyId, string challenge)> CreatePendingAsync(Guid userId, string name, CancellationToken ct = default);

    /// <summary>
    /// Gets pending passkey data from cache.
    /// </summary>
    Task<PendingPasskeyData?> GetPendingAsync(Guid userId, Guid passkeyId, CancellationToken ct = default);

    /// <summary>
    /// Deletes pending passkey from cache.
    /// </summary>
    Task DeletePendingAsync(Guid userId, Guid passkeyId, CancellationToken ct = default);
}

public record PendingPasskeyData(Guid Id, Guid UserId, string Name, string Challenge, DateTimeOffset CreatedAt);

public class PendingPasskeyStore(IDistributedCache cache) : IPendingPasskeyStore
{
    private static readonly TimeSpan PendingPasskeyTtl = TimeSpan.FromMinutes(15);

    private static string GetCacheKey(Guid userId, Guid passkeyId) => $"passkey:pending:{userId}:{passkeyId}";

    public async Task<(Guid passkeyId, string challenge)> CreatePendingAsync(Guid userId, string name, CancellationToken ct = default)
    {
        var passkeyId = Guid.CreateVersion7();
        var challenge = Convert.ToBase64String(OtpSecurity.GenerateSalt(32));
        
        var data = new PendingPasskeyData(passkeyId, userId, name, challenge, DateTimeOffset.UtcNow);
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        
        var cacheKey = GetCacheKey(userId, passkeyId);
        await cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = PendingPasskeyTtl
        }, ct);

        return (passkeyId, challenge);
    }

    public async Task<PendingPasskeyData?> GetPendingAsync(Guid userId, Guid passkeyId, CancellationToken ct = default)
    {
        var cacheKey = GetCacheKey(userId, passkeyId);
        var json = await cache.GetStringAsync(cacheKey, ct);
        
        if (json is null)
            return null;

        return System.Text.Json.JsonSerializer.Deserialize<PendingPasskeyData>(json);
    }

    public async Task DeletePendingAsync(Guid userId, Guid passkeyId, CancellationToken ct = default)
    {
        var cacheKey = GetCacheKey(userId, passkeyId);
        await cache.RemoveAsync(cacheKey, ct);
    }
}
