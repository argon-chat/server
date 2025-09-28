namespace Argon.Api.Features.CoreLogic.Otp;

using Argon.Services;

public static class OtpExtensions
{
    public static void AddOtpCodes(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IOtpService, OtpService>();
    }
}

public interface IOtpService
{
    Task       SendAsync(SendOtpRequest req, string ip, string? requestId = null, CancellationToken ct = default);
    Task<bool> VerifyAsync(VerifyOtpRequest req, CancellationToken ct = default);
}

public sealed class OtpService(IArgonCacheDatabase cache, IClusterClient clusterClient) : IOtpService
{

    private static readonly TimeSpan OtpTtl         = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Cooldown       = TimeSpan.FromSeconds(30);
    private const           int      MaxAttempts    = 5;
    private const           int      HourlyPerEmail = 5;
    private const           int      HourlyPerIp    = 20;

    private static string KeyActive(string email, OtpPurpose p)   => $"otp:{p}:{email.ToLowerInvariant()}:active";
    private static string KeyCooldown(string email, OtpPurpose p) => $"otp:{p}:{email.ToLowerInvariant()}:cooldown";
    private static string KeyRlEmail(string email)                => $"rl:otp:email:{email.ToLowerInvariant()}:hour";
    private static string KeyRlIp(string ip)                      => $"rl:otp:ip:{ip}:hour";

    public async Task SendAsync(SendOtpRequest req, string ip, string? requestId = null, CancellationToken ct = default)
    {
        var activeKey   = KeyActive(req.Email, req.Purpose);
        var cooldownKey = KeyCooldown(req.Email, req.Purpose);
        var rlEmailKey  = KeyRlEmail(req.Email);
        var rlIpKey     = KeyRlIp(ip);

        if (await cache.KeyExistsAsync(cooldownKey, ct))
            return;

        if (!await CheckRateLimitAsync(rlEmailKey, HourlyPerEmail, TimeSpan.FromHours(1), ct))
            return;
        if (!await CheckRateLimitAsync(rlIpKey, HourlyPerIp, TimeSpan.FromHours(1), ct))
            return;

        var code = OtpSecurity.GenerateNumericCode(6);
        var salt = OtpSecurity.GenerateSalt(16);
        var hash = OtpSecurity.ComputeHmac(salt, code);

        var record = new OtpRecord(
            Convert.ToBase64String(hash),
            Convert.ToBase64String(salt),
            DateTimeOffset.UtcNow.Add(OtpTtl),
            MaxAttempts,
            requestId,
            req.DeviceId
        );

        await cache.StringSetAsync(activeKey, JsonConvert.SerializeObject(record), OtpTtl, ct);
        await cache.StringSetAsync(cooldownKey, "1", Cooldown, ct);

        var emailGrain = clusterClient.GetGrain<IEmailManager>(Guid.NewGuid());
        await emailGrain.SendOtpCodeAsync(req.Email, code, OtpTtl);
    }

    public async Task<bool> VerifyAsync(VerifyOtpRequest req, CancellationToken ct = default)
    {
        var activeKey = KeyActive(req.Email, req.Purpose);
        var json      = await cache.StringGetAsync(activeKey, ct);
        if (json is null)
            return false;

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
        {
            await cache.KeyDeleteAsync(activeKey, ct);
        }
        else
        {
            var updated = rec with { AttemptsLeft = left };
            await cache.StringSetAsync(activeKey, JsonConvert.SerializeObject(updated),
                rec.Expiry - DateTimeOffset.UtcNow, ct);
        }

        return false;
    }

    private async Task<bool> CheckRateLimitAsync(string key, int max, TimeSpan window, CancellationToken ct)
    {
        var raw                                      = await cache.StringGetAsync(key, ct);
        if (!int.TryParse(raw, out var count)) count = 0;
        count++;

        if (count == 1)
            await cache.StringSetAsync(key, count.ToString(), window, ct);
        else
            await cache.StringSetAsync(key, count.ToString(), ct);

        return count <= max;
    }
}