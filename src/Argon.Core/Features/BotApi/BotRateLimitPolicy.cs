namespace Argon.Features.BotApi;

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

public sealed class BotRateLimitOptions
{
    public const string SectionName = "BotApi:RateLimits";

    /// <summary>Maximum concurrent requests per bot (concurrency limiter, no timer).</summary>
    public int MaxConcurrency { get; set; } = 10;

    /// <summary>Per-interface sliding window limits. Key = interface name (e.g. "IMessages").</summary>
    public Dictionary<string, RateWindowOptions> Interfaces { get; set; } = new()
    {
        ["IMessages"]     = new() { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) },
        ["IInteractions"] = new() { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) },
        ["ICommands"]     = new() { PermitLimit =  60, Window = TimeSpan.FromMinutes(1) },
        ["IChannels"]     = new() { PermitLimit =  60, Window = TimeSpan.FromMinutes(1) },
        ["ISpaces"]       = new() { PermitLimit =  30, Window = TimeSpan.FromMinutes(1) },
        ["IMembers"]      = new() { PermitLimit =  30, Window = TimeSpan.FromMinutes(1) },
        ["IVoice"]        = new() { PermitLimit =  20, Window = TimeSpan.FromMinutes(1) },
        ["IBotSelf"]      = new() { PermitLimit =  15, Window = TimeSpan.FromMinutes(1) },
        ["ICalls"]        = new() { PermitLimit =  20, Window = TimeSpan.FromMinutes(1) },
        ["IVoiceEgress"]  = new() { PermitLimit =  10, Window = TimeSpan.FromMinutes(1) },
        ["IEvents"]       = new() { PermitLimit =   5, Window = TimeSpan.FromMinutes(1) },
    };
}

public sealed class RateWindowOptions
{
    public int      PermitLimit { get; set; } = 60;
    public TimeSpan Window      { get; set; } = TimeSpan.FromMinutes(1);
}

public static class BotRateLimitExtensions
{
    private const int SegmentsPerWindow = 6;

    public static IServiceCollection AddBotRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BotRateLimitOptions>(configuration.GetSection(BotRateLimitOptions.SectionName));

        services.AddRateLimiter(options =>
        {
            var cfg = new BotRateLimitOptions();
            configuration.GetSection(BotRateLimitOptions.SectionName).Bind(cfg);

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.ContentType = "application/json";

                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
                    ? retryAfterValue.TotalSeconds
                    : 1.0;

                context.HttpContext.Response.Headers["Retry-After"]           = ((int)Math.Ceiling(retryAfter)).ToString();
                context.HttpContext.Response.Headers["X-RateLimit-Remaining"] = "0";

                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    error       = "rate_limited",
                    retry_after = retryAfter,
                    message     = "You are being rate limited."
                }, ct);
            };

            // Global concurrency limiter — runs BEFORE any per-endpoint policy.
            // No timers, just an atomic counter. Non-bot requests get NoLimiter (zero cost).
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var botId = context.User?.FindFirst("bot_id")?.Value;
                if (botId is null)
                    return RateLimitPartition.GetNoLimiter(string.Empty);

                return RateLimitPartition.GetConcurrencyLimiter(botId, _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = cfg.MaxConcurrency,
                    QueueLimit  = 0,
                });
            });

            // Per-interface sliding window — AutoReplenishment=false eliminates timers.
            // Window advances lazily when the next request arrives after the segment boundary.
            foreach (var (interfaceName, window) in cfg.Interfaces)
            {
                options.AddPolicy($"Bot_{interfaceName}", context =>
                {
                    var botId = context.User?.FindFirst("bot_id")?.Value;
                    if (botId is null)
                        return RateLimitPartition.GetNoLimiter(string.Empty);

                    return RateLimitPartition.GetSlidingWindowLimiter($"{botId}:{interfaceName}", _ =>
                        new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit       = window.PermitLimit,
                            Window            = window.Window,
                            SegmentsPerWindow = SegmentsPerWindow,
                            QueueLimit        = 0,
                            AutoReplenishment = false,
                        });
                });
            }
        });

        return services;
    }
}
