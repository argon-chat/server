namespace Argon.Api.Features.CoreLogic.Otp;

using Argon.Features.Integrations.Phones;
using Services;
using OtpNet;

public static class OtpExtensions
{
    public static void AddOtpCodes(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IOtpService, OtpService>();
        builder.Services.AddSingleton<ITotpKeyStore, TotpKeyStore>();
        builder.Services.AddSingleton<IOtpStrategy, PhoneOtpStrategy>();
        builder.Services.AddSingleton<IOtpStrategy, TotpOtpStrategy>();
        builder.Services.AddSingleton<IOtpStrategy, EmailOtpStrategy>();
    }
}

public enum ArgonAuthMode
{
    EmailPassword,
    EmailOtp,
    EmailPasswordOtp
}

public interface IOtpService
{
    Task       SendAsync(SendOtpRequest req, string ip, string? requestId = null, CancellationToken ct = default);
    Task<bool> VerifyAsync(VerifyOtpRequest req, CancellationToken ct = default);
}

public sealed class OtpService(IEnumerable<IOtpStrategy> strategies, ILogger<OtpService> logger) : IOtpService
{
    private readonly IReadOnlyDictionary<OtpMethod, IOtpStrategy> _map = strategies.ToDictionary(s => s.Method);

    public async Task SendAsync(SendOtpRequest req, string ip, string? requestId = null, CancellationToken ct = default)
    {
        if (!_map.TryGetValue(req.Method, out var strategy))
        {
            logger.LogError("No OTP strategy registered for {method}", req.Method);
            return;
        }

        await strategy.SendAsync(req, ip, ct);
    }

    public async Task<bool> VerifyAsync(VerifyOtpRequest req, CancellationToken ct = default)
    {
        if (!_map.TryGetValue(req.Method, out var strategy))
        {
            logger.LogError("No OTP strategy registered for {method}", req.Method);
            return false;
        }

        return await strategy.VerifyAsync(req, ct);
    }
}

public interface IOtpStrategy
{
    OtpMethod  Method { get; }
    Task       SendAsync(SendOtpRequest req, string ip, CancellationToken ct);
    Task<bool> VerifyAsync(VerifyOtpRequest req, CancellationToken ct);
}

public sealed class TotpOtpStrategy(ITotpKeyStore keyStore, ILogger<TotpOtpStrategy> logger) : IOtpStrategy
{
    public OtpMethod Method => OtpMethod.Totp;

    public Task SendAsync(SendOtpRequest req, string ip, CancellationToken ct)
    {
        logger.LogInformation("TOTP in use for {user}", req.Target);
        return Task.CompletedTask;
    }

    public async Task<bool> VerifyAsync(VerifyOtpRequest req, CancellationToken ct)
    {
        var secret = await keyStore.GetSecret(req.UserId, ct);
        if (secret is null) return false;

        var totp = new Totp(secret);
        return totp.VerifyTotp(req.Code, out _, VerificationWindow.RfcSpecifiedNetworkDelay);
    }
}

public sealed class PhoneOtpStrategy(IPhoneProvider phoneProvider, ILogger<PhoneOtpStrategy> logger) : IOtpStrategy
{
    public OtpMethod Method => OtpMethod.Phone;

    public async Task SendAsync(SendOtpRequest req, string ip, CancellationToken ct)
    {
        await phoneProvider.SendCode(req.Target, ip, req.DeviceId, req.Purpose.ToString());
        logger.LogInformation("Sent OTP via SMS to {phone}", req.Target);
    }

    public async Task<bool> VerifyAsync(VerifyOtpRequest req, CancellationToken ct)
    {
        var result = await phoneProvider.VerifyCode(req.Target, req.DeviceId ?? "", req.Code);
        return result.verifyResult == VerifyStatus.Verified;
    }
}

public sealed class EmailOtpStrategy(
    IArgonCacheDatabase cache,
    IClusterClient clusterClient,
    ILogger<EmailOtpStrategy> logger
) : IOtpStrategy
{
    public OtpMethod Method => OtpMethod.Email;

    private static readonly TimeSpan OtpTtl         = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Cooldown       = TimeSpan.FromSeconds(30);
    private const           int      MaxAttempts    = 10;
    private const           int      HourlyPerEmail = 10;
    private const           int      HourlyPerIp    = 30;

    private static string KeyActive(string email, OtpPurpose p)   => $"otp:{p}:{email.ToLowerInvariant()}:active";
    private static string KeyCooldown(string email, OtpPurpose p) => $"otp:{p}:{email.ToLowerInvariant()}:cooldown";
    private static string KeyRlEmail(string email)                => $"rl:otp:email:{email.ToLowerInvariant()}:hour";
    private static string KeyRlIp(string ip)                      => $"rl:otp:ip:{ip}:hour";

    public async Task SendAsync(SendOtpRequest req, string ip, CancellationToken ct)
    {
        var activeKey   = KeyActive(req.Target, req.Purpose);
        var cooldownKey = KeyCooldown(req.Target, req.Purpose);
        var rlEmailKey  = KeyRlEmail(req.Target);
        var rlIpKey     = KeyRlIp(ip);

        if (await cache.KeyExistsAsync(cooldownKey, ct))
        {
            logger.LogWarning("Cooldown active for {email}", req.Target);
            return;
        }

        if (!await CheckRateLimitAsync(rlEmailKey, HourlyPerEmail, TimeSpan.FromHours(1), ct))
        {
            logger.LogWarning("Rate limited by email {email}", req.Target);
            return;
        }

        if (!await CheckRateLimitAsync(rlIpKey, HourlyPerIp, TimeSpan.FromHours(1), ct))
        {
            logger.LogWarning("Rate limited by IP {ip}", ip);
            return;
        }

        var code = OtpSecurity.GenerateNumericCode(6);
        var salt = OtpSecurity.GenerateSalt(16);
        var hash = OtpSecurity.ComputeHmac(salt, code);

        var record = new OtpRecord(
            Convert.ToBase64String(hash),
            Convert.ToBase64String(salt),
            DateTimeOffset.UtcNow.Add(OtpTtl),
            MaxAttempts,
            null,
            req.DeviceId
        );

        await cache.StringSetAsync(activeKey, JsonConvert.SerializeObject(record), OtpTtl, ct);
        await cache.StringSetAsync(cooldownKey, "1", Cooldown, ct);

        var emailGrain = clusterClient.GetGrain<IEmailManager>(Guid.NewGuid());
        await emailGrain.SendOtpCodeAsync(req.Target, code, OtpTtl);
    }

    public async Task<bool> VerifyAsync(VerifyOtpRequest req, CancellationToken ct)
    {
        var activeKey = KeyActive(req.Target, req.Purpose);
        var json      = await cache.StringGetAsync(activeKey, ct);
        if (json is null) return false;

        var rec = JsonConvert.DeserializeObject<OtpRecord>(json);
        if (rec is null || rec.Expiry <= DateTimeOffset.UtcNow)
        {
            await cache.KeyDeleteAsync(activeKey, ct);
            return false;
        }

        if (!string.IsNullOrEmpty(rec.DeviceId) && rec.DeviceId != req.DeviceId)
            return false;

        var salt     = Convert.FromBase64String(rec.SaltBase64);
        var expected = Convert.FromBase64String(rec.HashBase64);
        var actual   = OtpSecurity.ComputeHmac(salt, req.Code);

        if (OtpSecurity.ConstantTimeEquals(actual, expected))
        {
            await cache.KeyDeleteAsync(activeKey, ct);
            return true;
        }

        var left = Math.Max(0, rec.AttemptsLeft - 1);
        if (left == 0)
            await cache.KeyDeleteAsync(activeKey, ct);
        else
            await cache.StringSetAsync(activeKey, JsonConvert.SerializeObject(rec with
                {
                    AttemptsLeft = left
                }),
                rec.Expiry - DateTimeOffset.UtcNow, ct);

        return false;
    }

    private async Task<bool> CheckRateLimitAsync(string key, int max, TimeSpan window, CancellationToken ct)
    {
        var count = (int)await cache.StringIncrementAsync(key, ct);
        if (count == 1)
            await cache.KeyExpireAsync(key, window, ct);
        return count <= max;
    }
}