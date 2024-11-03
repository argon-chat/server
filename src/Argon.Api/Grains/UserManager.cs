namespace Argon.Api.Grains;

using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using Entities;
using Helpers;
using Interfaces;
using MemoryPack;
using MessagePack;

public class UserManager(
    // [PersistentState("userServers", "OrleansStorage")]
    // IPersistentState<UserToServerRelations> userServerStore,
    IGrainFactory grainFactory,
    ILogger<UserManager> logger,
    ApplicationDbContext context
) : Grain, IUserManager
{
    public Task CreateUser(UserCredentialsInput input)
    {
        var user = new User
        {
            Email = input.Email,
            Username = input.Username,
            PhoneNumber = input.PhoneNumber,
            PasswordDigest = HashPassword(VerifyPassword(input.Password, input.PasswordConfirmation)),
            OTP = ""
        };
        user.AvatarUrl = Gravatar.GenerateGravatarUrl(user);
        context.Users.Add(user);
        return context.SaveChangesAsync();
    }

    public Task UpdateUser(UserCredentialsInput input)
    {
        var user = context.Users.First(u => u.Id == this.GetPrimaryKey());
        user.Email = input.Email;
        user.Username = input.Username ?? user.Username;
        user.PhoneNumber = input.PhoneNumber ?? user.PhoneNumber;
        user.PasswordDigest = HashPassword(VerifyPassword(input.Password, input.PasswordConfirmation)) ??
                              user.PasswordDigest;
        user.AvatarUrl = Gravatar.GenerateGravatarUrl(user);
        return context.SaveChangesAsync();
    }

    public Task DeleteUser()
    {
        var user = context.Users.First(u => u.Id == this.GetPrimaryKey());
        user.DeletedAt = DateTime.UtcNow;
        return context.SaveChangesAsync();
    }

    public Task GetUser()
    {
        return Task.CompletedTask;
    }

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
}

[Serializable]
[GenerateSerializer]
[MemoryPackable]
[Alias(nameof(JwtToken))]
public partial record struct JwtToken(string token);

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias(nameof(UserCredentialsInput))]
public sealed partial record UserCredentialsInput(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    [property: Id(0)]
    string Email,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    [property: Id(1)]
    string? Username,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    [property: Id(2)]
    string? PhoneNumber,
    [property: DataMember(Order = 3)]
    [property: MemoryPackOrder(3)]
    [property: Key(3)]
    [property: Id(3)]
    string? Password,
    [property: DataMember(Order = 4)]
    [property: MemoryPackOrder(4)]
    [property: Key(4)]
    [property: Id(4)]
    string? PasswordConfirmation,
    [property: DataMember(Order = 5)]
    [property: MemoryPackOrder(5)]
    [property: Key(5)]
    [property: Id(5)]
    bool GenerateOtp
);