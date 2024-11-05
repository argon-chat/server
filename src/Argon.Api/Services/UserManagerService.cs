namespace Argon.Api.Services;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Features.Jwt;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public class UserManagerService(
    ILogger<UserManagerService> logger,
    IOptions<JwtOptions>        jwt,
    IConfiguration              configuration)
{
    public Task<string> GenerateJwt(string email, Guid id)
    {
        var (issuer, audience, key, exp) = jwt.Value;
        var signingCredentials = new SigningCredentials(key: new SymmetricSecurityKey(key: Encoding.UTF8.GetBytes(s: key)),
                                                        algorithm: SecurityAlgorithms.HmacSha512Signature);
        var subject = new ClaimsIdentity(claims:
                                         [
                                             new Claim(type: "id",    value: id.ToString()),
                                             new Claim(type: "email", value: email)
                                         ]);
        var expires = DateTime.UtcNow.Add(value: exp);
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject            = subject,
            Expires            = expires,
            Issuer             = issuer,
            Audience           = audience,
            SigningCredentials = signingCredentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token        = tokenHandler.CreateToken(tokenDescriptor: tokenDescriptor);
        var jwtToken     = tokenHandler.WriteToken(token: token);
        if (jwtToken == null)
            throw new Exception(message: "Failed to generate token"); // TODO: Come up with application specific errors

        return Task.FromResult(result: jwtToken);
    }

    public async Task Validate(string username, string password)
    {
        await ValidateLength(str: username, obj: nameof(username), min: 3, max: 50);
        await ValidateLength(str: password, obj: nameof(password), min: 8, max: 32);
        await ValidatePasswordStrength(password: password);
    }


    private Task ValidatePasswordStrength(string password)
    {
        if (!password.Any(predicate: char.IsDigit))
        {
            throw new Exception(
                                message: "Password must contain at least one digit"); // TODO: Come up with application specific errors
        }

        if (!password.Any(predicate: char.IsUpper))
        {
            throw new Exception(
                                message: "Password must contain at least one uppercase letter"); // TODO: Come up with application specific errors
        }

        if (!password.Any(predicate: char.IsLower))
        {
            throw new Exception(
                                message: "Password must contain at least one lowercase letter"); // TODO: Come up with application specific errors
        }

        return Task.CompletedTask;
    }

    private Task ValidateLength(string str, string obj, int min, int max)
    {
        if (str.Length < min || str.Length > max)
            throw new Exception(message: $"{obj}: Invalid length"); // TODO: Come up with application specific errors

        return Task.CompletedTask;
    }
}