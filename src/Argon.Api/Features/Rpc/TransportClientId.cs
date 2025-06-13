namespace Argon.Services;

public record TransportClientId(Ulid id, Guid userId, string hash, Guid sessionId)
{
    public override string ToString()
        => $"{userId:N}.{id}.{hash}.{sessionId}";
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

public enum ExchangeTokenError
{
    NONE,
    BAD_KEY,
    INTEGRITY_FAILED,
    ALREADY_EXCHANGED
}

public interface ITransportExchange
{
    ValueTask<TransportClientId>  CreateExchangeKey(string token, Guid userId, string machineId, Guid sessionId);
    ValueTask<Either<TransportClientId, ExchangeTokenError>> ExchangeToken(string token, HttpContext httpContext);
}