namespace Argon.Api.Services;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Features.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public class UserManagerService(ILogger<UserManagerService> logger, IOptions<JwtOptions> jwt, IConfiguration configuration)
{
    public Task<string> GenerateJwt(string email, Guid id)
    {
        var (issuer, audience, key, exp) = jwt.Value;
        var signingCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha512Signature);
        var subject = new ClaimsIdentity([
            new Claim("id", id.ToString()),
            new Claim("email", email)
        ]);
        var expires = DateTime.UtcNow.Add(exp);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject            = subject,
            Expires            = expires,
            Issuer             = issuer,
            Audience           = audience,
            SigningCredentials = signingCredentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token        = tokenHandler.CreateToken(tokenDescriptor);
        var jwtToken     = tokenHandler.WriteToken(token);
        if (jwtToken == null)
            throw new Exception("Failed to generate token"); // TODO: Come up with application specific errors

        return Task.FromResult(jwtToken);
    }

    public async Task Validate(string username, string password)
    {
        await ValidateLength(username, nameof(username), 3, 50);
        await ValidateLength(password, nameof(password), 8, 32);
        await ValidatePasswordStrength(password);
    }


    private Task ValidatePasswordStrength(string password)
    {
        if (!password.Any(char.IsDigit))
            throw new Exception("Password must contain at least one digit"); // TODO: Come up with application specific errors

        if (!password.Any(char.IsUpper))
            throw new Exception("Password must contain at least one uppercase letter"); // TODO: Come up with application specific errors

        if (!password.Any(char.IsLower))
            throw new Exception("Password must contain at least one lowercase letter"); // TODO: Come up with application specific errors

        return Task.CompletedTask;
    }

    private Task ValidateLength(string str, string obj, int min, int max)
    {
        if (str.Length < min || str.Length > max)
            throw new Exception($"{obj}: Invalid length"); // TODO: Come up with application specific errors

        return Task.CompletedTask;
    }
}