namespace Argon.Services;

public interface IArgonCacheDatabase
{
    Task          StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default);
    Task          UpdateStringExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default);
    Task          StringSetAsync(string key, string value, CancellationToken ct = default);
    Task<string?> StringGetAsync(string key, CancellationToken ct = default);
    Task          KeyDeleteAsync(string key, CancellationToken ct = default);
    Task<bool>    KeyExistsAsync(string key, CancellationToken ct = default);

    Task<long> StringIncrementAsync(string key, CancellationToken ct = default);
    Task<string> KeyExpireAsync(string key, TimeSpan window, CancellationToken ct = default);


    IAsyncEnumerable<string> ScanKeysAsync(string pattern, CancellationToken ct = default);
}