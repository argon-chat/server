namespace Argon.Core.Features.CoreLogic.Passkeys;

using Api.Features.CoreLogic.Otp;
using Argon.Core.Features.Logic;
using Microsoft.Extensions.Caching.Distributed;

public interface IPendingPasskeyStore
{
    /// <summary>
    /// Creates a pending passkey registration and stores Fido2 options in cache.
    /// </summary>
    Task<Guid> CreatePendingAsync(Guid userId, string name, string optionsJson, CancellationToken ct = default);

    /// <summary>
    /// Gets pending passkey registration data from cache.
    /// </summary>
    Task<PendingPasskeyData?> GetPendingAsync(Guid userId, Guid passkeyId, CancellationToken ct = default);

    /// <summary>
    /// Deletes pending passkey registration from cache.
    /// </summary>
    Task DeletePendingAsync(Guid userId, Guid passkeyId, CancellationToken ct = default);

    /// <summary>
    /// Stores the current registration state (name + Fido2 options) for a user.
    /// Only one active registration per user at a time.
    /// </summary>
    Task StoreRegistrationStateAsync(Guid userId, string name, string optionsJson, CancellationToken ct = default);

    /// <summary>
    /// Gets the current registration state for a user.
    /// </summary>
    Task<PendingPasskeyData?> GetRegistrationStateAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Deletes the current registration state after use.
    /// </summary>
    Task DeleteRegistrationStateAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Stores Fido2 assertion options for validation flow.
    /// </summary>
    Task StoreValidationOptionsAsync(Guid userId, string optionsJson, CancellationToken ct = default);

    /// <summary>
    /// Gets stored assertion options for validation flow.
    /// </summary>
    Task<string?> GetValidationOptionsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Deletes stored assertion options after use.
    /// </summary>
    Task DeleteValidationOptionsAsync(Guid userId, CancellationToken ct = default);
}

public record PendingPasskeyData(Guid Id, Guid UserId, string Name, string OptionsJson, DateTimeOffset CreatedAt);

public class PendingPasskeyStore(IDistributedCache cache) : IPendingPasskeyStore
{
    private static readonly TimeSpan PendingPasskeyTtl = TimeSpan.FromMinutes(5);

    private static string GetCacheKey(Guid userId, Guid passkeyId) => $"passkey:pending:{userId}:{passkeyId}";
    private static string GetRegistrationCacheKey(Guid userId) => $"passkey:registration:{userId}";
    private static string GetValidationCacheKey(Guid userId) => $"passkey:validation:{userId}";

    public async Task<Guid> CreatePendingAsync(Guid userId, string name, string optionsJson, CancellationToken ct = default)
    {
        var passkeyId = Guid.CreateVersion7();
        
        var data = new PendingPasskeyData(passkeyId, userId, name, optionsJson, DateTimeOffset.UtcNow);
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        
        var cacheKey = GetCacheKey(userId, passkeyId);
        await cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = PendingPasskeyTtl
        }, ct);

        return passkeyId;
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

    public async Task StoreRegistrationStateAsync(Guid userId, string name, string optionsJson, CancellationToken ct = default)
    {
        var data = new PendingPasskeyData(Guid.Empty, userId, name, optionsJson, DateTimeOffset.UtcNow);
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        var cacheKey = GetRegistrationCacheKey(userId);
        await cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = PendingPasskeyTtl
        }, ct);
    }

    public async Task<PendingPasskeyData?> GetRegistrationStateAsync(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = GetRegistrationCacheKey(userId);
        var json = await cache.GetStringAsync(cacheKey, ct);
        if (json is null) return null;
        return System.Text.Json.JsonSerializer.Deserialize<PendingPasskeyData>(json);
    }

    public async Task DeleteRegistrationStateAsync(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = GetRegistrationCacheKey(userId);
        await cache.RemoveAsync(cacheKey, ct);
    }

    public async Task StoreValidationOptionsAsync(Guid userId, string optionsJson, CancellationToken ct = default)
    {
        var cacheKey = GetValidationCacheKey(userId);
        await cache.SetStringAsync(cacheKey, optionsJson, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = PendingPasskeyTtl
        }, ct);
    }

    public async Task<string?> GetValidationOptionsAsync(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = GetValidationCacheKey(userId);
        return await cache.GetStringAsync(cacheKey, ct);
    }

    public async Task DeleteValidationOptionsAsync(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = GetValidationCacheKey(userId);
        await cache.RemoveAsync(cacheKey, ct);
    }
}
