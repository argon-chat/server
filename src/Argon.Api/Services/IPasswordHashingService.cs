namespace Argon.Api.Services;

using System.Security.Cryptography;
using System.Text;
using Entities;
using Helpers;

public interface IPasswordHashingService
{
    string? HashPassword(string?     password,      string? passwordConfirmation);
    bool    VerifyPassword(string?   inputPassword, User    user);
    bool    ValidatePassword(string? password,      string? passwordDigest);
    bool    VerifyOtp(string?        inputOtp,      string? userOtp);
    string  GenerateOtp();
}

public class PasswordHashingService : IPasswordHashingService
{
    public string? HashPassword(string? password, string? passwordConfirmation)
    {
        if (password is null || passwordConfirmation is null) return null;
        if (password != passwordConfirmation) throw new Exception(message: "Password confirmation does not match password");
        using var sha256 = SHA256.Create();
        var       bytes  = Encoding.UTF8.GetBytes(s: password);
        var       hash   = sha256.ComputeHash(buffer: bytes);
        return Convert.ToBase64String(inArray: hash);
    }

    public bool VerifyPassword(string? inputPassword, User user)
        => ValidatePassword(password: inputPassword, passwordDigest: user.PasswordDigest) || VerifyOtp(inputOtp: inputPassword, userOtp: user.OTP);

    public bool ValidatePassword(string? password, string? passwordDigest)
    {
        if (password is null || passwordDigest is null) return false;
        return HashPassword(password: password, passwordConfirmation: password) == passwordDigest;
    }

    public bool VerifyOtp(string? inputOtp, string? userOtp)
    {
        if (inputOtp is null || userOtp is null) return false;
        return inputOtp == userOtp;
    }

    public string GenerateOtp()
        => SecureRandom.Hex(n: 3);
}