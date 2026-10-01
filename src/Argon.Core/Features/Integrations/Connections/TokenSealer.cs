namespace Argon.Features.Integrations.Connections;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// A provider's tokens as the grain holds them: never on the wire, never in a log line.
/// </summary>
public sealed record ProviderToken(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string Scopes)
{
    public bool ExpiresWithin(TimeSpan lead, DateTimeOffset now)
        => ExpiresAt is { } at && at - now <= lead;

    public bool HasScope(string scope)
        => Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);
}

/// <summary>
/// AES-256-GCM over a token record, with the key version in the blob so keys can be rotated.
/// </summary>
/// <remarks>
/// <para>Layout: <c>version(1) ‖ nonce(12) ‖ ciphertext ‖ tag(16)</c>. The additional data is the
/// connection id and the provider, so a blob moved to another row does not open. The key comes
/// from <see cref="ConnectionsOptions.TokenKey"/> — Vault — and earlier keys stay in
/// <see cref="ConnectionsOptions.RetiredTokenKeys"/> until every row sealed under them has been
/// re-sealed, which <see cref="TryUnseal"/> reports through <c>staleKey</c>.</para>
///
/// <para>A sibling of the AEAD helpers in <c>DevTeamsGrain</c> rather than a use of the Aegis
/// data-protection ring: that ring belongs to the identity server and is not something this side
/// rotates on a schedule.</para>
/// </remarks>
public sealed partial class TokenSealer
{
    private const int KeySize   = 32;
    private const int NonceSize = 12;
    private const int TagSize   = 16;

    private readonly byte[]?                  current;
    private readonly byte                     currentVersion;
    private readonly Dictionary<byte, byte[]> keys = new();

    public TokenSealer(IOptions<ConnectionsOptions> options)
    {
        var o = options.Value;

        currentVersion = (byte)Math.Clamp(o.TokenKeyVersion, 1, 255);

        if (TryDecodeKey(o.TokenKey, out var key))
        {
            current              = key;
            keys[currentVersion] = key;
        }

        foreach (var (version, retired) in o.RetiredTokenKeys)
        {
            if (int.TryParse(version, out var v) && v is >= 1 and <= 255 && TryDecodeKey(retired, out var k))
                keys.TryAdd((byte)v, k);
        }
    }

    public bool IsConfigured   => current is not null;
    public byte CurrentVersion => currentVersion;

    public static bool IsUsableKey(string? base64) => TryDecodeKey(base64, out _);

    private static bool TryDecodeKey(string? base64, out byte[] key)
    {
        key = [];

        if (string.IsNullOrWhiteSpace(base64))
            return false;

        var buffer = new byte[KeySize + 8];

        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var written) || written != KeySize)
            return false;

        key = buffer[..KeySize];
        return true;
    }

    public byte[] Seal(ProviderToken token, Guid connectionId, ConnectionProvider provider)
    {
        if (current is null)
            throw new InvalidOperationException("Connections:TokenKey is not configured; a token cannot be stored");

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Payload(token.AccessToken, token.RefreshToken,
            token.ExpiresAt?.ToUnixTimeSeconds(), token.Scopes), PayloadContext.Default.Payload);

        var blob = new byte[1 + NonceSize + plaintext.Length + TagSize];

        blob[0] = currentVersion;

        var nonce      = blob.AsSpan(1, NonceSize);
        var ciphertext = blob.AsSpan(1 + NonceSize, plaintext.Length);
        var tag        = blob.AsSpan(1 + NonceSize + plaintext.Length, TagSize);

        RandomNumberGenerator.Fill(nonce);

        using var cipher = new AesGcm(current, TagSize);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag, Aad(connectionId, provider));

        CryptographicOperations.ZeroMemory(plaintext);

        return blob;
    }

    /// <summary>
    /// Opens a blob. False for anything that does not verify — a foreign key version, a moved row,
    /// a corrupt blob — never an exception, so the caller treats it like a token that is gone.
    /// </summary>
    public bool TryUnseal(byte[]? blob, Guid connectionId, ConnectionProvider provider, out ProviderToken? token, out bool staleKey)
    {
        token    = null;
        staleKey = false;

        if (blob is null || blob.Length < 1 + NonceSize + TagSize)
            return false;

        if (!keys.TryGetValue(blob[0], out var key))
            return false;

        var nonce      = blob.AsSpan(1, NonceSize);
        var ciphertext = blob.AsSpan(1 + NonceSize, blob.Length - 1 - NonceSize - TagSize);
        var tag        = blob.AsSpan(blob.Length - TagSize, TagSize);
        var plaintext  = new byte[ciphertext.Length];

        try
        {
            using var cipher = new AesGcm(key, TagSize);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, Aad(connectionId, provider));

            var payload = JsonSerializer.Deserialize(plaintext, PayloadContext.Default.Payload);

            if (payload is null || string.IsNullOrEmpty(payload.A))
                return false;

            token = new ProviderToken(payload.A, payload.R,
                payload.E is { } e ? DateTimeOffset.FromUnixTimeSeconds(e) : null, payload.S ?? "");
            staleKey = blob[0] != currentVersion;
            return true;
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] Aad(Guid connectionId, ConnectionProvider provider)
    {
        var aad = new byte[18];
        connectionId.TryWriteBytes(aad);
        BitConverter.TryWriteBytes(aad.AsSpan(16), (ushort)provider);
        return aad;
    }

    private sealed record Payload(
        [property: JsonPropertyName("a")] string A,
        [property: JsonPropertyName("r")] string? R,
        [property: JsonPropertyName("e")] long? E,
        [property: JsonPropertyName("s")] string? S);

    [JsonSerializable(typeof(Payload))]
    private sealed partial class PayloadContext : JsonSerializerContext;
}
