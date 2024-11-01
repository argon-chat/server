namespace Argon.Api.Grains;

using System.Security.Cryptography;
using System.Text;
using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Persistence.States;
using Services;

public class UserAuthorizationManager(
    ApplicationDbContext context,
    UserManagerService managerService,
    IGrainFactory grainFactory
) : IGrain, IUserAuthorizationManager
{
    public async Task<JwtToken> Authorize(UserCredentialsInput input)
    {
        var user = await context.Users.FirstOrDefaultAsync(user =>
            user.Email == input.Email || user.PhoneNumber == input.PhoneNumber || user.Username == input.Username);

        if (user is null)
            throw new Exception("User not found with given credentials"); // TODO: implement application errors
        return await GenerateJwt(user);
    }

    private async Task<JwtToken> GenerateJwt(User User) => new(await managerService.GenerateJwt(User.Username, User.Id));

    public async Task Register(UserCredentialsInput input)
    {
        // TODO: implement email and phone number verification
        // TODO: implement username, email and phone number uniqueness verification
        
        context.Users.Add(new User
        {
            Email = input.Email,
            Username = input.Username,
            PhoneNumber = input.PhoneNumber,
            PasswordDigest = HashPassword(VerifyPassword(input.Password, input.PasswordConfirmation)),
            AvatarUrl = ""
        });

        await context.SaveChangesAsync();
    }

    public async Task<UserStorageDto> GetMe(Guid id) => await context.Users.FirstAsync(user => user.Id == id);

    private static string HashPassword(string input) // TODO: replace with an actual secure hashing mechanism
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    private string VerifyPassword(string InputPassword, string InputPasswordConfirmation)
    {
        if (InputPassword != InputPasswordConfirmation)
            throw new ArgumentException("Are you axueli tam?"); // TODO: implement application errors
        
        // TODO: implement password strength verification
        
        return InputPassword;
    }
}