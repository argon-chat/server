namespace Argon.Services.L1L2;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// A cached value and the token that identifies its content.
/// </summary>
/// <remarks>
/// The token is stored with the value rather than derived on demand, because deriving it means
/// serialising and hashing and the whole point is to do neither once a caller already has the
/// answer. Filling the cache pays for it once; every read after that compares two strings.
/// </remarks>
public sealed record Versioned<T>(string Version, T Value);

/// <summary>
/// Content tokens for the parts of a space a client caches.
/// </summary>
/// <remarks>
/// A content hash, not a counter and not a token minted per cache fill. A counter would be a second
/// source of truth to keep in step with the invalidation that already exists, and a minted token
/// would change every time the entry expired — which is every two minutes — so a client that had
/// changed nothing would re-download everything on a timer.
/// </remarks>
public static class SpaceReadVersion
{
    /// <summary>
    /// Sixteen hex characters of SHA-256 over the value as the cache itself would write it. Truncated
    /// because this is a change detector, not a security boundary, and it travels on every request.
    /// </summary>
    public static string Of<T>(T value)
        => Hash(JsonSerializer.SerializeToUtf8Bytes(value, IonJson.Options));

    /// <summary>
    /// Folds several tokens into one, for an answer that depends on more than one cached thing.
    /// </summary>
    public static string Combine(params ReadOnlySpan<string> parts)
    {
        var builder = new StringBuilder();

        // Separated, so that ("ab", "c") and ("a", "bc") cannot fold to the same token.
        foreach (var part in parts)
            builder.Append(part).Append('\u001f');

        return Hash(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes))[..16];
}
