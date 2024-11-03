namespace Argon.Api.Helpers;

using System.Security.Cryptography;
using System.Text;
using Entities;
using Grains.Interfaces;

public static class UserHelper
{
    public static string? HashPassword(string? input) // TODO: replace with an actual secure hashing mechanism
    {
        if (input is null) return null;
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    public static string? VerifyPassword(string? InputPassword, string? InputPasswordConfirmation)
    {
        if (InputPassword is null || InputPasswordConfirmation is null) return null;
        if (InputPassword != InputPasswordConfirmation)
            throw new ArgumentException("Are you axueli tam?"); // TODO: implement application errors

        // TODO: implement password strength verification

        return InputPassword;
    }

    public static Task<User> GenerateOtp(User User)
    {
        User.OTP = SecureRandom.Hex(3);
        return Task.FromResult(User);
    }

    public static Task ValidatePassword(string? InputPassword, User user)
    {
        if (user.PasswordDigest is null)
        {
            if (InputPassword != user.OTP)
                throw new Exception("sebya brutforsi sabaken"); // TODO: implement application errors

            return Task.CompletedTask;
        }

        if (HashPassword(InputPassword) != user.PasswordDigest)
            throw new Exception("sebya brutforsi sabaken"); // TODO: implement application errors

        return Task.CompletedTask;
    }
}