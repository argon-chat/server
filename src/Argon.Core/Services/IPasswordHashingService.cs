namespace Argon.Services;

using Features.Auth;
using Microsoft.Extensions.Options;

public interface IPasswordHashingService
{
    const string OneTimePassKey = $"{nameof(IPasswordHashingService)}.onetime";
    string?      HashPassword(string? password);
    bool         VerifyPassword(string? inputPassword, UserEntity user);
    bool         ValidatePassword(string? password, string? passwordDigest);
    bool         VerifyOtp(string? inputOtp, string? userOtp);

    /// <summary>
    /// Whether this digest was produced by something other than what is configured now.
    /// </summary>
    /// <remarks>
    /// The answer is only actionable right after a successful verification, because that is the only
    /// moment the plaintext exists to hash again. Migration is therefore something logging in does,
    /// not something a background job can do.
    /// </remarks>
    bool NeedsRehash(string? passwordDigest);
}

/// <summary>
/// Password digests, in whatever scheme they were written, verified against the scheme they name.
/// </summary>
/// <remarks>
/// Every digest says how it was made, so the store can hold several schemes at once and a user moves
/// between them without anyone knowing their password. The format follows the shape PHC strings use:
/// <code>$pbkdf2-sha512$i=210000$&lt;salt&gt;$&lt;hash&gt;</code>
/// <para>
/// A digest that does not start with <c>$</c> is the original scheme: a bare, unsalted, single-pass
/// SHA-256. It is kept only so existing accounts can still sign in, and only until they do —
/// <see cref="NeedsRehash"/> reports it as stale from the first successful login. It should never be
/// configured for new passwords, which is why <see cref="PasswordHashAlgorithm.LegacySha256"/> is
/// rejected in the options rather than merely discouraged.
/// </para>
/// <para>
/// Nothing here puts a password, a salt or a digest on the heap. Secrets that live in a
/// <c>byte[]</c> stay wherever the collector last moved them until something overwrites that memory,
/// which may be long after the request; a stack buffer is gone when the frame is, and is wiped
/// before that anyway. The bounds below exist so the buffers can be stack buffers at all.
/// </para>
/// </remarks>
public class PasswordHashingService(
    IOptions<PasswordHashingOptions> options,
    ILogger<IPasswordHashingService> logger) : IPasswordHashingService
{
    /// <summary>
    /// Caps on what a stored digest may claim, so that reading one cannot be talked into a stack
    /// buffer of its choosing. Comfortably above anything <see cref="PasswordHashingOptions"/> allows
    /// to be written.
    /// </summary>
    private const int MaxSaltBytes     = 64;
    private const int MaxHashBytes     = 128;
    private const int MaxPasswordBytes = 1024;
    private const int LegacyHashBytes  = 32;

    public string? HashPassword(string? password)
    {
        if (password is null)
            return null;

        var settings = options.Value;

        Span<byte> secret = stackalloc byte[MaxPasswordBytes];

        if (!TryEncode(password, secret, out var secretLength))
            return null;

        Span<byte> salt = stackalloc byte[MaxSaltBytes];
        Span<byte> hash = stackalloc byte[MaxHashBytes];

        salt = salt[..settings.SaltBytes];
        hash = hash[..settings.HashBytes];

        try
        {
            RandomNumberGenerator.Fill(salt);
            Rfc2898DeriveBytes.Pbkdf2(secret[..secretLength], salt, hash,
                settings.Iterations, HashOf(settings.Algorithm));

            return Format(Moniker(settings.Algorithm), settings.Iterations, salt, hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    public bool VerifyPassword(string? inputPassword, UserEntity user)
        => ValidatePassword(inputPassword, user.PasswordDigest);

    public bool ValidatePassword(string? password, string? passwordDigest)
    {
        if (password is null || passwordDigest is null || passwordDigest.Length == 0)
            return false;

        Span<byte> secret = stackalloc byte[MaxPasswordBytes];

        if (!TryEncode(password, secret, out var secretLength))
            return false;

        try
        {
            return passwordDigest[0] == '$'
                ? VerifyDerived(secret[..secretLength], passwordDigest)
                : VerifyLegacy(secret[..secretLength], passwordDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public bool NeedsRehash(string? passwordDigest)
    {
        if (passwordDigest is null)
            return false;

        if (passwordDigest.Length == 0 || passwordDigest[0] != '$')
            return true;

        Span<byte> salt = stackalloc byte[MaxSaltBytes];
        Span<byte> hash = stackalloc byte[MaxHashBytes];

        if (!TryParse(passwordDigest, out var algorithm, out var iterations, ref salt, ref hash))
            return true;

        var settings = options.Value;

        // Parameters as well as the algorithm: raising the iteration count is how this keeps up with
        // hardware, and it only means anything if existing digests move up to it.
        return algorithm != settings.Algorithm || iterations < settings.Iterations;
    }

    public bool VerifyOtp(string? inputOtp, string? userOtp)
    {
        if (inputOtp is null || userOtp is null)
            return false;

        Span<byte> given    = stackalloc byte[MaxPasswordBytes];
        Span<byte> expected = stackalloc byte[MaxPasswordBytes];

        if (!TryEncode(inputOtp, given, out var givenLength) ||
            !TryEncode(userOtp, expected, out var expectedLength))
            return false;

        try
        {
            return CryptographicOperations.FixedTimeEquals(given[..givenLength], expected[..expectedLength]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(given);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private bool VerifyDerived(ReadOnlySpan<byte> secret, ReadOnlySpan<char> digest)
    {
        Span<byte> salt     = stackalloc byte[MaxSaltBytes];
        Span<byte> expected = stackalloc byte[MaxHashBytes];
        Span<byte> actual   = stackalloc byte[MaxHashBytes];

        if (!TryParse(digest, out var algorithm, out var iterations, ref salt, ref expected))
        {
            logger.LogError("A stored password digest is not in any format this service can read");
            return false;
        }

        actual = actual[..expected.Length];

        try
        {
            Rfc2898DeriveBytes.Pbkdf2(secret, salt, actual, iterations, HashOf(algorithm));

            // Fixed-time: a comparison that returns early tells whoever is guessing how much of the
            // digest they got right.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
        }
    }

    /// <summary>
    /// The original scheme: SHA-256 of the password, no salt, one pass.
    /// </summary>
    /// <remarks>
    /// Unsalted means one precomputed table covers every account at once, and one pass means a
    /// commodity GPU tries billions of candidates a second. This exists to let those accounts in one
    /// last time so their digest can be replaced.
    /// </remarks>
    private static bool VerifyLegacy(ReadOnlySpan<byte> secret, ReadOnlySpan<char> digest)
    {
        Span<byte> expected = stackalloc byte[LegacyHashBytes];
        Span<byte> actual   = stackalloc byte[LegacyHashBytes];

        if (!Convert.TryFromBase64Chars(digest, expected, out var written) || written != LegacyHashBytes)
            return false;

        SHA256.HashData(secret, actual);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Reads a digest into the buffers the caller supplied, narrowing each to what it actually holds.
    /// </summary>
    /// <remarks>
    /// <c>ref Span</c> rather than <c>out</c>: the buffers belong to the caller's frame, so this can
    /// only ever reslice them, never hand back storage of its own.
    /// </remarks>
    private static bool TryParse(
        ReadOnlySpan<char> digest,
        out PasswordHashAlgorithm algorithm,
        out int iterations,
        ref Span<byte> salt,
        ref Span<byte> hash)
    {
        algorithm  = default;
        iterations = 0;

        var rest = digest;

        // A well-formed digest starts with '$', so the first segment is empty.
        if (!TryNext(ref rest, out var empty) || !empty.IsEmpty)
            return false;

        if (!TryNext(ref rest, out var moniker))
            return false;

        if (moniker.SequenceEqual("pbkdf2-sha256"))
            algorithm = PasswordHashAlgorithm.Pbkdf2HmacSha256;
        else if (moniker.SequenceEqual("pbkdf2-sha512"))
            algorithm = PasswordHashAlgorithm.Pbkdf2HmacSha512;
        else
            return false;

        if (!TryNext(ref rest, out var cost) || !cost.StartsWith("i=") ||
            !int.TryParse(cost[2..], out iterations) || iterations <= 0)
            return false;

        if (!TryNext(ref rest, out var encodedSalt) ||
            !Convert.TryFromBase64Chars(encodedSalt, salt, out var saltLength) || saltLength == 0)
            return false;

        // Whatever is left is the hash: it is the last segment and carries no separator of its own.
        if (rest.IsEmpty || !Convert.TryFromBase64Chars(rest, hash, out var hashLength) || hashLength == 0)
            return false;

        salt = salt[..saltLength];
        hash = hash[..hashLength];
        return true;
    }

    private static bool TryNext(ref ReadOnlySpan<char> rest, out ReadOnlySpan<char> segment)
    {
        var at = rest.IndexOf('$');

        if (at < 0)
        {
            segment = rest;
            rest    = [];
            return !segment.IsEmpty;
        }

        segment = rest[..at];
        rest    = rest[(at + 1)..];
        return true;
    }

    private static string Format(ReadOnlySpan<char> moniker, int iterations, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> hash)
    {
        Span<char> buffer = stackalloc char[512];
        var        at     = 0;

        buffer[at++] = '$';
        moniker.CopyTo(buffer[at..]);
        at += moniker.Length;

        "$i=".CopyTo(buffer[at..]);
        at += 3;

        iterations.TryFormat(buffer[at..], out var written);
        at += written;

        buffer[at++] = '$';
        Convert.TryToBase64Chars(salt, buffer[at..], out written);
        at += written;

        buffer[at++] = '$';
        Convert.TryToBase64Chars(hash, buffer[at..], out written);
        at += written;

        return new string(buffer[..at]);
    }

    private static bool TryEncode(ReadOnlySpan<char> value, Span<byte> destination, out int written)
    {
        written = 0;

        return Encoding.UTF8.GetByteCount(value) <= destination.Length
            && Encoding.UTF8.TryGetBytes(value, destination, out written);
    }

    private static HashAlgorithmName HashOf(PasswordHashAlgorithm algorithm)
        => algorithm switch
        {
            PasswordHashAlgorithm.Pbkdf2HmacSha256 => HashAlgorithmName.SHA256,
            PasswordHashAlgorithm.Pbkdf2HmacSha512 => HashAlgorithmName.SHA512,
            _ => throw new InvalidOperationException(
                $"{algorithm} cannot derive a digest; it exists only to read old ones")
        };

    private static ReadOnlySpan<char> Moniker(PasswordHashAlgorithm algorithm)
        => algorithm switch
        {
            PasswordHashAlgorithm.Pbkdf2HmacSha256 => "pbkdf2-sha256",
            PasswordHashAlgorithm.Pbkdf2HmacSha512 => "pbkdf2-sha512",
            _ => throw new InvalidOperationException($"{algorithm} has no digest format of its own")
        };
}
