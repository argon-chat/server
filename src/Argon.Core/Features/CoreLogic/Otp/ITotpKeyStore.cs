namespace Argon.Api.Features.CoreLogic.Otp;

using Microsoft.Extensions.Caching.Distributed;
using OtpNet;

public interface ITotpKeyStore
{
    Task<byte[]?> GetSecret(Guid userId, CancellationToken ct = default);
    Task<byte[]>  CreateSecret(Guid userId, CancellationToken ct = default);
    Task          SaveSecret(Guid userId, byte[] secret, CancellationToken ct = default);
    Task          DeleteSecret(Guid userId, CancellationToken ct = default);
    
    /// <summary>
    /// Creates a pending secret stored in cache. Returns the secret bytes.
    /// </summary>
    Task<byte[]> CreatePendingSecret(Guid userId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets pending secret from cache.
    /// </summary>
    Task<byte[]?> GetPendingSecret(Guid userId, CancellationToken ct = default);
    
    /// <summary>
    /// Deletes pending secret from cache.
    /// </summary>
    Task DeletePendingSecret(Guid userId, CancellationToken ct = default);
}

public class TotpKeyStore(IDbContextFactory<ApplicationDbContext> dbFactory, IDistributedCache cache) : ITotpKeyStore
{
    private static readonly TimeSpan PendingSecretTtl = TimeSpan.FromMinutes(15);
    
    private static string GetPendingCacheKey(Guid userId) => $"totp:pending:{userId}";

    public async Task<byte[]?> GetSecret(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var base64 = await db.Users
           .Where(u => u.Id == userId)
           .Select(u => u.TotpSecret)
           .FirstOrDefaultAsync(ct);

        return base64 is null ? null : Convert.FromBase64String(base64);
    }

    public async Task<byte[]> CreateSecret(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var secret = KeyGeneration.GenerateRandomKey();

        await db.Users
           .Where(u => u.Id == userId)
           .ExecuteUpdateAsync(u => u.SetProperty(x => x.TotpSecret, Convert.ToBase64String(secret)), ct);

        return secret;
    }

    public async Task SaveSecret(Guid userId, byte[] secret, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await db.Users
           .Where(u => u.Id == userId)
           .ExecuteUpdateAsync(u => u.SetProperty(x => x.TotpSecret, Convert.ToBase64String(secret)), ct);
    }

    public async Task DeleteSecret(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await db.Users
           .Where(u => u.Id == userId)
           .ExecuteUpdateAsync(u => u.SetProperty(x => x.TotpSecret, (string?)null), ct);
    }

    public async Task<byte[]> CreatePendingSecret(Guid userId, CancellationToken ct = default)
    {
        var secret = KeyGeneration.GenerateRandomKey();
        var cacheKey = GetPendingCacheKey(userId);
        
        await cache.SetAsync(cacheKey, secret, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = PendingSecretTtl
        }, ct);

        return secret;
    }

    public async Task<byte[]?> GetPendingSecret(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = GetPendingCacheKey(userId);
        return await cache.GetAsync(cacheKey, ct);
    }

    public async Task DeletePendingSecret(Guid userId, CancellationToken ct = default)
    {
        var cacheKey = GetPendingCacheKey(userId);
        await cache.RemoveAsync(cacheKey, ct);
    }
}