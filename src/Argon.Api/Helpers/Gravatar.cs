namespace Argon.Api.Helpers;

using System.Security.Cryptography;
using System.Text;
using Entities;

public static class Gravatar
{
    public static string GenerateGravatarUrl(User user, int size = 200)
    {
        var emailHash = GetMd5Hash(input: user.Email.Trim().ToLower());
        return $"https://www.gravatar.com/avatar/{emailHash}?s={size}";
    }

    private static string GetMd5Hash(string input)
    {
        using var md5        = MD5.Create();
        var       inputBytes = Encoding.ASCII.GetBytes(s: input);
        var       hashBytes  = md5.ComputeHash(buffer: inputBytes);
        var       sb         = new StringBuilder();
        foreach (var t in hashBytes)
            sb.Append(value: t.ToString(format: "x2"));

        return sb.ToString();
    }
}