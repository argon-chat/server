namespace Argon.Api.Helpers;

using System.Security.Cryptography;

public static class SecureRandom
{
    public static string Hex(int n)
    {
        var buffer = new byte[n];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(buffer);
        }

        return BitConverter.ToString(buffer).Replace("-", "").ToLower();
    }
}