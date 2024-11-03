namespace Argon.Api.Grains;

using System.Security.Cryptography;
using System.Text;
using Entities;
using Helpers;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Persistence.States;
using Services;

public class UserAuthorizationManager(
    ILogger<UserAuthorizationManager> logger,
    ApplicationDbContext context,
    UserManagerService managerService,
    IGrainFactory grainFactory
) : Grain, IUserAuthorizationManager
{
    public async Task<JwtToken> Authorize(UserCredentialsInput input)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == input.Email);

        if (user is null)
            throw new Exception("User not found with given credentials"); // TODO: implement application errors

        if (input.GenerateOtp) return await GenerateOtp(user);

        await ValidatePassword(input.Password, user);
        await GenerateOtp(user); // regenerate OTP after successful login
        return await GenerateJwt(user);
    }

    public async Task Register(UserCredentialsInput input)
    {
        // TODO: implement email and phone number verification
        // TODO: implement username, email and phone number uniqueness verification
        var user = new User
        {
            Email = input.Email,
            Username = input.Username,
            PhoneNumber = input.PhoneNumber,
            PasswordDigest = HashPassword(VerifyPassword(input.Password, input.PasswordConfirmation)),
        };
        user.AvatarUrl = Gravatar.GenerateGravatarUrl(user);
        context.Users.Add(user);

        await context.SaveChangesAsync();
    }

    public async Task<UserStorageDto> GetById(Guid id) => await context.Users.FirstAsync(user => user.Id == id);

    private static string? HashPassword(string? input) // TODO: replace with an actual secure hashing mechanism
    {
        if (input is null) return null;
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    private string? VerifyPassword(string? InputPassword, string? InputPasswordConfirmation)
    {
        if (InputPassword is null || InputPasswordConfirmation is null) return null;
        if (InputPassword != InputPasswordConfirmation)
            throw new ArgumentException("Are you axueli tam?"); // TODO: implement application errors

        // TODO: implement password strength verification

        return InputPassword;
    }

    private async Task<JwtToken> GenerateOtp(User User)
    {
        User.OTP = SecureRandom.Hex(3);
        logger.LogInformation($"OTP for {User.Username} is {User.OTP}");
        await context.SaveChangesAsync();
        return new JwtToken("");
    }

    private Task ValidatePassword(string? InputPassword, User user)
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

    private async Task<JwtToken> GenerateJwt(User User) =>
        new(await managerService.GenerateJwt(User.Email, User.Id));
}