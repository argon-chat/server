namespace Argon.Features.BotApi;

using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

public sealed class BotRateLimitOptions
{
    public const string SectionName = "BotApi:RateLimits";

    /// <summary>Global rate limit per bot (token bucket).</summary>
    public RateBucketOptions Global { get; set; } = new()
    {
        TokenLimit          = 30,
        TokensPerPeriod     = 30,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1)
    };

    /// <summary>Per-interface overrides. Key = interface name (e.g. "IMessages").</summary>
    public Dictionary<string, RateBucketOptions> Interfaces { get; set; } = new()
    {
        ["IMessages"] = new RateBucketOptions
        {
            TokenLimit          = 20,
            TokensPerPeriod     = 20,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1)
        },
        ["ISpaces"] = new RateBucketOptions
        {
            TokenLimit          = 5,
            TokensPerPeriod     = 5,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1)
        }
    };
}

public sealed class RateBucketOptions
{
    public int      TokenLimit          { get; set; } = 30;
    public int      TokensPerPeriod     { get; set; } = 30;
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);
}

public static class BotRateLimitExtensions
{
    public const string GlobalPolicyName = "BotGlobal";

    public static IServiceCollection AddBotRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BotRateLimitOptions>(configuration.GetSection(BotRateLimitOptions.SectionName));

        services.AddRateLimiter(options =>
        {
            var rateLimitConfig = new BotRateLimitOptions();
            configuration.GetSection(BotRateLimitOptions.SectionName).Bind(rateLimitConfig);

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.ContentType = "application/json";

                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue)
                    ? retryAfterValue.TotalSeconds
                    : 1.0;

                context.HttpContext.Response.Headers["Retry-After"]          = ((int)Math.Ceiling(retryAfter)).ToString();
                context.HttpContext.Response.Headers["X-RateLimit-Remaining"] = "0";

                await context.HttpContext.Response.WriteAsJsonAsync(new
                {
                    error       = "rate_limited",
                    retry_after = retryAfter,
                    message     = "You are being rate limited."
                }, ct);
            };

            // Global per-bot rate limit
            options.AddPolicy(GlobalPolicyName, context =>
            {
                var botId = context.User?.FindFirst("bot_id")?.Value;
                if (botId is null)
                    return RateLimitPartition.GetNoLimiter(string.Empty);

                return RateLimitPartition.GetTokenBucketLimiter(botId, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit          = rateLimitConfig.Global.TokenLimit,
                    TokensPerPeriod     = rateLimitConfig.Global.TokensPerPeriod,
                    ReplenishmentPeriod = rateLimitConfig.Global.ReplenishmentPeriod,
                    QueueLimit          = 0,
                    AutoReplenishment   = true,
                });
            });

            // Per-interface rate limit policies
            foreach (var (interfaceName, bucketOptions) in rateLimitConfig.Interfaces)
            {
                options.AddPolicy($"Bot_{interfaceName}", context =>
                {
                    var botId = context.User?.FindFirst("bot_id")?.Value;
                    if (botId is null)
                        return RateLimitPartition.GetNoLimiter(string.Empty);

                    return RateLimitPartition.GetTokenBucketLimiter($"{botId}:{interfaceName}", _ =>
                        new TokenBucketRateLimiterOptions
                        {
                            TokenLimit          = bucketOptions.TokenLimit,
                            TokensPerPeriod     = bucketOptions.TokensPerPeriod,
                            ReplenishmentPeriod = bucketOptions.ReplenishmentPeriod,
                            QueueLimit          = 0,
                            AutoReplenishment   = true,
                        });
                });
            }
        });

        return services;
    }
}

/// <summary>
/// Endpoint filter that adds rate limit response headers to successful responses.
/// </summary>
public sealed class RateLimitHeadersFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        var response = context.HttpContext.Response;
        if (!response.Headers.ContainsKey("X-RateLimit-Limit"))
        {
            // These will be populated by rate limiting middleware on rejection;
            // on success we add informational headers
            response.Headers["X-RateLimit-Policy"] = "bot";
        }

        return result;
    }
}
