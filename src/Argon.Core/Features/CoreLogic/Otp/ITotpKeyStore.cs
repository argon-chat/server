namespace Argon.Api.Features.CoreLogic.Otp;

using OtpNet;

public interface ITotpKeyStore
{
    Task<byte[]?> GetSecret(Guid userId, CancellationToken ct = default);
    Task<byte[]>  CreateSecret(Guid userId, CancellationToken ct = default);
    Task          DeleteSecret(Guid userId, CancellationToken ct = default);
}

public class TotpKeyStore(IDbContextFactory<ApplicationDbContext> dbFactory) : ITotpKeyStore
{
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

    public async Task DeleteSecret(Guid userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        await db.Users
           .Where(u => u.Id == userId)
           .ExecuteUpdateAsync(u => u.SetProperty(x => x.TotpSecret, (string?)null), ct);
    }
}