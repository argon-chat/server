namespace Argon.Api.Features.Otp;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using OtpNet;
using Services;

public record OneTimeOtpSettings
{
    public string      ProtectorId     { get; init; }
    public string      SecretPart      { get; init; }
    public OtpHashMode HashMode        { get; init; }
    public int         Duration        { get; init; }
    public float       RotationRadians { get; init; }
}

public static class PasswordHashingExtensions
{
    public static IServiceCollection AddOtpCodes(this IHostApplicationBuilder hostBuilder)
    {
        var services = hostBuilder.Services;
        services.Configure<OneTimeOtpSettings>(hostBuilder.Configuration.GetSection("Totp"));
        services.AddKeyedSingleton<OtpGenerator>(IPasswordHashingService.OneTimePassKey);
        return services;
    }
}

public class OtpGenerator(IDataProtectionProvider dataProtection, IOptions<OneTimeOtpSettings> otpSettings)
{
    public unsafe string GenerateKey(Guid userId, DateTimeOffset creationTime)
    {
        var settings = otpSettings.Value;
        var date     = DateOnly.FromDateTime(creationTime.UtcDateTime);
        var time     = TimeOnly.FromDateTime(creationTime.UtcDateTime);
        var hashKey = dataProtection.CreateProtector(settings.ProtectorId).ToTimeLimitedDataProtector()
           .Protect($"{date:d}:{time.Hour:x8}:{userId}", DateTimeOffset.Now.AddMinutes(settings.Duration));
        var hashKeyLen = Encoding.UTF8.GetByteCount(hashKey);
        var secretLen  = Encoding.UTF8.GetByteCount(settings.SecretPart);

        Span<byte> mem = stackalloc byte[hashKeyLen + secretLen];

        Encoding.UTF8.GetBytes(hashKey, mem.Slice(0, hashKeyLen));
        Encoding.UTF8.GetBytes(settings.SecretPart, mem.Slice(hashKeyLen, secretLen));

        Rotate(mem, settings.RotationRadians);

        return new Totp(mem.ToArray(), (int)TimeSpan.FromMinutes(settings.Duration).TotalSeconds, settings.HashMode).ComputeTotp();
    }

    private static void Rotate(Span<byte> input, float radians)
    {
        if (input.Length == 0)
            return;
        var degrees = (int)MathF.Floor(radians * (180.0f / MathF.PI) % 360f);
        var length  = input.Length;
        degrees %= length;
        if (degrees < 0)
            degrees += length;
        if (degrees == 0)
            return;
        input.Reverse();
        input[..degrees].Reverse();
        input[degrees..].Reverse();
    }
}

public record OtpCode(string Code)
{
    public string Hashed => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Code)));
}