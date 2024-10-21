namespace Argon.Api.Grains;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using BCrypt.Net;
using Interfaces;
using Microsoft.IdentityModel.Tokens;
using Persistence.States;

public class UserManager(
    ILogger<UserManager> logger,
    IConfiguration configuration,
    [PersistentState("users", "OrleansStorage")]
    IPersistentState<UserStorage> userStore) : Grain, IUserManager
{
    public async Task<UserStorageDto> Create(string password)
    {
        var username = this.GetPrimaryKeyString();
        await Validate(username, password);

        userStore.State.Id = Guid.NewGuid();
        userStore.State.Username = username;
        userStore.State.Password = BCrypt.HashPassword(password);
        await userStore.WriteStateAsync();
        return userStore.State;
    }

    public async Task<UserStorageDto> Get()
    {
        await userStore.ReadStateAsync();
        return userStore.State;
    }

    public Task<UserStorageDto> GetById(string id)
    {
        var sql = @"with decoded_payload as (
    select (encode(payloadbinary, 'escape'))::jsonb as payload
    from orleansstorage
    where graintypestring = 'users'
)
select payload
from decoded_payload
where payload->>'Id' = ?;";

        return Task.FromResult(new UserStorageDto());
    }

    public Task<string> Authenticate(string password)
    {
        var match = BCrypt.Verify(password, userStore.State.Password);

        if (!match)
            throw new Exception("Invalid credentials"); // TODO: Come up with application specific errors

        return GenerateJwt();
    }

    private Task<string> GenerateJwt()
    {
        var issuer = configuration["Jwt:Issuer"];
        var audience = configuration["Jwt:Audience"];
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(configuration["Jwt:Key"] ?? throw new ArgumentNullException("Jwt:Key")));
        var signingCredentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha512Signature
        );

        var subject = new ClaimsIdentity(new[]
        {
            new Claim("id", userStore.State.Id.ToString()),
            new Claim("username", userStore.State.Username)
        });
        var expires = DateTime.UtcNow.AddDays(228);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = subject,
            Expires = expires,
            Issuer = issuer,
            Audience = audience,
            SigningCredentials = signingCredentials
        };
        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        var jwtToken = tokenHandler.WriteToken(token);
        if (jwtToken == null)
            throw new Exception("Failed to generate token"); // TODO: Come up with application specific errors

        return Task.FromResult(jwtToken);
    }

    private async Task Validate(string username, string password)
    {
        await EnsureUnique();
        await ValidateLength(username, nameof(username), 3, 50);
        await ValidateLength(password, nameof(password), 8, 32);
        await ValidatePasswordStrength(password);
    }

    private Task ValidatePasswordStrength(string password)
    {
        if (!password.Any(char.IsDigit))
            throw new Exception(
                "Password must contain at least one digit"); // TODO: Come up with application specific errors

        if (!password.Any(char.IsUpper))
            throw new Exception(
                "Password must contain at least one uppercase letter"); // TODO: Come up with application specific errors

        if (!password.Any(char.IsLower))
            throw new Exception(
                "Password must contain at least one lowercase letter"); // TODO: Come up with application specific errors

        return Task.CompletedTask;
    }

    private Task ValidateLength(string str, string obj, int min, int max)
    {
        if (str.Length < min || str.Length > max)
            throw new Exception($"{obj}: Invalid length"); // TODO: Come up with application specific errors

        return Task.CompletedTask;
    }

    private Task EnsureUnique()
    {
        if (userStore.State.Id != Guid.Empty)
            throw new Exception("User already exists"); // TODO: Come up with application specific errors

        return Task.CompletedTask;
    }
}