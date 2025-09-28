namespace Argon.Api.Features.CoreLogic.Otp;

public static class OtpSecurity
{
    public static string GenerateNumericCode(int digits = 6)
    {
        var max = (int)Math.Pow(10, digits);
        var num = RandomNumberGenerator.GetInt32(0, max);
        return num.ToString(new string('0', digits));
    }

    public static byte[] GenerateSalt(int size = 16)
    {
        var salt = new byte[size];
        RandomNumberGenerator.Fill(salt);
        return salt;
    }

    public static byte[] ComputeHmac(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    public static bool ConstantTimeEquals(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        var diff                                = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}