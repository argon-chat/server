namespace Argon.Api.Services;

using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

public class UserManagerService(
    ILogger<UserManagerService> logger,
    IConfiguration configuration)
{
    public Task<string> GenerateJwt(string username, Guid id)
    {
        var issuer = configuration["Jwt:Issuer"];
        var audience = configuration["Jwt:Audience"];
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(configuration["Jwt:Key"] ?? throw new ArgumentNullException("Jwt:Key")));
        var signingCredentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha512Signature
        );
        var subject = new ClaimsIdentity([
            new Claim("id", id.ToString()),
            new Claim("username", username)
        ]);
        var expires = DateTime.UtcNow.AddDays(configuration.GetValue<int>("Jwt:Expires"));
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

    [Conditional("RELEASE")]
    public void Validate(string username, string password)
    {
        ValidateLength(username, nameof(username), 3, 50);
        ValidateLength(password, nameof(password), 8, 32);
        ValidatePasswordStrength(password);
    }

    private void ValidatePasswordStrength(string password)
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
    }

    private void ValidateLength(string str, string obj, int min, int max)
    {
        if (str.Length < min || str.Length > max)
            throw new Exception($"{obj}: Invalid length"); // TODO: Come up with application specific errors
    }
}