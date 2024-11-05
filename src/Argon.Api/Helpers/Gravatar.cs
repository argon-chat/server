namespace Argon.Api.Helpers;

using System.Security.Cryptography;
using System.Text;
using Entities;

public static class Gravatar
{
    public static string GenerateGravatarUrl(User user, int size = 200)
    {
        var emailHash = GetMd5Hash(user.Email.Trim().ToLower());
        return $"https://www.gravatar.com/avatar/{emailHash}?s={size}";
    }

    private static string GetMd5Hash(string input)
    {
        using var md5        = MD5.Create();
        var       inputBytes = Encoding.ASCII.GetBytes(input);
        var       hashBytes  = md5.ComputeHash(inputBytes);
        var       sb         = new StringBuilder();
        foreach (var t in hashBytes)
            sb.Append(t.ToString("x2"));

        return sb.ToString();
    }
}