namespace Argon.Services;

public class TransportExchange(IOptions<TransportOptions> options, IArgonCacheDatabase db, IServiceProvider provider)
    : ITransportExchange
{
    public async ValueTask<TransportClientId> CreateExchangeKey(string token, Guid userId, Guid machineId)
    {
        var id = Ulid.NewUlid();

        await db.StringSetAsync(id.ToString(), token, TimeSpan.FromSeconds(60));

        return new TransportClientId(id, userId, GenerateHash(id, userId));
    }


    private string GenerateHash(Ulid id, Guid userId)
    {
        using var hmac      = new HMACSHA256(Encoding.UTF8.GetBytes(options.Value.Exchange.HashKey));
        var       input     = $"{id}{userId}";
        var       hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
        return $"{new Ulid(new Guid(hashBytes.Take(16).ToArray()))}@{new Ulid(new Guid(hashBytes.Skip(16).ToArray()))}";
    }

    public async ValueTask<Either<TransportClientId, ExchangeTokenError>> ExchangeToken(string token, HttpContext httpContext)
    {
        var keys = token.Split('.');

        if (keys.Length != 3)
            return ExchangeTokenError.BAD_KEY;

        var (uid, id, hash) = (keys[0], keys[1], keys[2]);

        if (!Ulid.TryParse(id, out var tokenId) || !Guid.TryParse(uid, out var userId))
            return ExchangeTokenError.BAD_KEY;
        if (!GenerateHash(tokenId, userId).Equals(hash))
            return ExchangeTokenError.INTEGRITY_FAILED;

        if (!await db.KeyExistsAsync(tokenId.ToString()))
            return ExchangeTokenError.ALREADY_EXCHANGED;

        await db.KeyDeleteAsync(tokenId.ToString());

        return new TransportClientId(tokenId, userId, hash);
    }
}