namespace Argon.Services;

using StackExchange.Redis;

public record TransportClientId(Ulid id, Guid userId, string hash)
{
    public override string ToString()
        => $"{userId:N}.{id}.{hash}";
}

public class TransportExchangeOptions
{
    public string HashKey { get; set; }
}

public class TransportOptions
{
    public TransportExchangeOptions Exchange { get; set; }

    public string Upgrade                { get; set; }
    public string CertificateFingerprint { get; set; }
}

public class TransportExchange(IOptions<TransportOptions> options, IConnectionMultiplexer multiplexer, IServiceProvider provider)
    : ITransportExchange
{
    public async ValueTask<TransportClientId> CreateExchangeKey(string token, Guid userId, Guid machineId)
    {
        var id = Ulid.NewUlid();

        var db = multiplexer.GetDatabase();
        await db.StringSetAsync(id.ToString(), new RedisValue(token!), TimeSpan.FromSeconds(60));

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
        var db = multiplexer.GetDatabase();

        if (!db.KeyExists(tokenId.ToString()))
            return ExchangeTokenError.ALREADY_EXCHANGED;

        await db.KeyDeleteAsync(tokenId.ToString());

        return new TransportClientId(tokenId, userId, hash);
    }
}

public enum ExchangeTokenError
{
    NONE,
    BAD_KEY,
    INTEGRITY_FAILED,
    ALREADY_EXCHANGED
}

public interface ITransportExchange
{
    ValueTask<TransportClientId>  CreateExchangeKey(string token, Guid userId, Guid machineId);
    ValueTask<Either<TransportClientId, ExchangeTokenError>> ExchangeToken(string token, HttpContext httpContext);
}