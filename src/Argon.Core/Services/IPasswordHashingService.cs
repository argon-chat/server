namespace Argon.Services;

using Konscious.Security.Cryptography;
using System.Security.Cryptography;

public interface IPasswordHashingService
{
    const string OneTimePassKey = $"{nameof(IPasswordHashingService)}.onetime";
    string?      HashPassword(string? password);
    bool         VerifyPassword(string? inputPassword, UserEntity user);
    bool         ValidatePassword(string? password, string? passwordDigest);
    bool         VerifyOtp(string? inputOtp, string? userOtp);
}

public class PasswordHashingService(ILogger<IPasswordHashingService> logger) : IPasswordHashingService
{
    // Argon2id parameters (recommended by OWASP)
    private const int SaltSize = 16;        // 128 bits
    private const int HashSize = 32;        // 256 bits
    private const int Iterations = 3;       // Time cost
    private const int MemorySize = 65536;   // 64 MB
    private const int DegreeOfParallelism = 4;

    public string? HashPassword(string? password)
    {
        if (password is null) return null;

        try
        {
            // Generate a cryptographically secure random salt
            var salt = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }

            // Hash the password using Argon2id
            var hash = HashPasswordInternal(password, salt);

            // Combine salt and hash for storage: salt (16 bytes) + hash (32 bytes)
            var combined = new byte[SaltSize + HashSize];
            Buffer.BlockCopy(salt, 0, combined, 0, SaltSize);
            Buffer.BlockCopy(hash, 0, combined, SaltSize, HashSize);

            return Convert.ToBase64String(combined);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Cannot compute Argon2 hash, dropping operation..");
            return null;
        }
    }

    public bool VerifyPassword(string? inputPassword, UserEntity user) =>
        ValidatePassword(inputPassword, user.PasswordDigest);

    public bool ValidatePassword(string? password, string? passwordDigest)
    {
        if (password is null || passwordDigest is null) return false;

        try
        {
            // Check if this is a legacy SHA-256 hash (32 bytes when decoded from base64)
            var storedBytes = Convert.FromBase64String(passwordDigest);
            if (storedBytes.Length == 32)
            {
                // Legacy SHA-256 hash - validate using old method
                logger.LogWarning("Using legacy SHA-256 password validation. Please rehash passwords.");
                return ValidatePasswordLegacy(password, passwordDigest);
            }

            // Modern Argon2 hash validation
            if (storedBytes.Length != SaltSize + HashSize)
            {
                logger.LogWarning("Invalid password digest length: {Length}", storedBytes.Length);
                return false;
            }

            // Extract salt and stored hash
            var salt = new byte[SaltSize];
            var storedHash = new byte[HashSize];
            Buffer.BlockCopy(storedBytes, 0, salt, 0, SaltSize);
            Buffer.BlockCopy(storedBytes, SaltSize, storedHash, 0, HashSize);

            // Hash the input password with the same salt
            var inputHash = HashPasswordInternal(password, salt);

            // Compare hashes using constant-time comparison
            return CryptographicOperations.FixedTimeEquals(inputHash, storedHash);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating password");
            return false;
        }
    }

    private byte[] HashPasswordInternal(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = DegreeOfParallelism,
            Iterations = Iterations,
            MemorySize = MemorySize
        };

        return argon2.GetBytes(HashSize);
    }

    // Legacy SHA-256 validation for backward compatibility
    private bool ValidatePasswordLegacy(string password, string passwordDigest)
    {
        using var sha256 = SHA256.Create();
        var bytesLen = Encoding.UTF8.GetByteCount(password);
        Span<byte> source = stackalloc byte[bytesLen];
        Span<byte> dest = stackalloc byte[32];
        Encoding.UTF8.GetBytes(password, source);

        if (!sha256.TryComputeHash(source, dest, out var written))
        {
            logger.LogCritical("Cannot compute sha256 hash for legacy validation.");
            return false;
        }

        var legacyHash = Convert.ToBase64String(dest[..written]);
        // Use constant-time comparison to prevent timing attacks even for legacy hashes
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(legacyHash),
            Encoding.UTF8.GetBytes(passwordDigest));
    }

    public bool VerifyOtp(string? inputOtp, string? userOtp)
    {
        if (inputOtp is null || userOtp is null) return false;
        return inputOtp == userOtp;
    }
}