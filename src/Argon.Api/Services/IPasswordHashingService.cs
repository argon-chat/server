namespace Argon.Services;

using Features.Otp;

public interface IPasswordHashingService
{
    const string OneTimePassKey = $"{nameof(IPasswordHashingService)}.onetime";
    string?      HashPassword(string? password);
    bool         VerifyPassword(string? inputPassword, User user);
    bool         ValidatePassword(string? password, string? passwordDigest);
    bool         VerifyOtp(string? inputOtp, string? userOtp);
    OtpCode      GenerateOtp(Guid userId);
}

public class PasswordHashingService([FromKeyedServices(IPasswordHashingService.OneTimePassKey)] OtpGenerator otpGenerator, 
    ILogger<IPasswordHashingService> logger) : IPasswordHashingService
{
    public unsafe string? HashPassword(string? password)
    {
        if (password is null) return null;
        using var  sha256   = SHA256.Create();
        var        bytesLen = Encoding.UTF8.GetByteCount(password);
        Span<byte> source   = stackalloc byte[bytesLen];
        Span<byte> dest     = stackalloc byte[32];
        Encoding.UTF8.GetBytes(password, source);

        if (!sha256.TryComputeHash(source, dest, out var written))
        {
            logger.LogCritical($"Cannot compute sha256 hash, dropping operation..");
            return null;
        }

        return Convert.ToBase64String(dest[..written]);
    }

    public bool VerifyPassword(string? inputPassword, User user) =>
        ValidatePassword(inputPassword, user.PasswordDigest) || VerifyOtp(inputPassword, user.OtpHash);

    public bool ValidatePassword(string? password, string? passwordDigest)
    {
        if (password is null || passwordDigest is null) return false;
        return HashPassword(password) == passwordDigest;
    }

    public bool VerifyOtp(string? inputOtp, string? userOtp)
    {
        if (inputOtp is null || userOtp is null) return false;
        return inputOtp == userOtp;
    }

    public OtpCode GenerateOtp(Guid userId) => new(otpGenerator.GenerateKey(userId, DateTimeOffset.Now));
}