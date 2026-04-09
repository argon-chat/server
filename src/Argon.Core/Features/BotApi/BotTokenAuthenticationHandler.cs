namespace Argon.Features.BotApi;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Hybrid;

public sealed class BotTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory                              logger,
    System.Text.Encodings.Web.UrlEncoder        encoder,
    IServiceProvider                            serviceProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "BotToken";
    public const string AuthHeaderPrefix = "Bot ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader) || string.IsNullOrWhiteSpace(authHeader))
            return AuthenticateResult.NoResult();

        var authString = authHeader.ToString();
        if (!authString.StartsWith(AuthHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = authString[AuthHeaderPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.Fail("Empty bot token");

        var cache = serviceProvider.GetRequiredService<HybridCache>();

        var botInfo = await cache.GetOrCreateAsync(
            $"bot:auth:{ComputeTokenHash(token)}",
            token,
            static async (token, ct) =>
            {
                // This is a factory function, we don't have access to 'this' or
                // serviceProvider here — but HybridCache expects a static lambda.
                // We'll move the DB lookup outside.
                return (BotAuthCacheEntry?)null;
            },
            new HybridCacheEntryOptions
            {
                Expiration      = TimeSpan.FromMinutes(5),
                LocalCacheExpiration = TimeSpan.FromMinutes(1)
            });

        // If cache returned null, do a real lookup and cache it
        if (botInfo is null)
        {
            botInfo = await ResolveBotFromToken(token);
            if (botInfo is null)
                return AuthenticateResult.Fail("Invalid bot token");

            await cache.SetAsync(
                $"bot:auth:{ComputeTokenHash(token)}",
                botInfo,
                new HybridCacheEntryOptions
                {
                    Expiration           = TimeSpan.FromMinutes(5),
                    LocalCacheExpiration = TimeSpan.FromMinutes(1)
                });
        }

        if (botInfo.IsRestricted)
            return AuthenticateResult.Fail("Bot is restricted");

        var claims = new[]
        {
            new System.Security.Claims.Claim("bot_id", botInfo.AppId.ToString()),
            new System.Security.Claims.Claim("app_id", botInfo.AppId.ToString()),
            new System.Security.Claims.Claim("team_id", botInfo.TeamId.ToString()),
            new System.Security.Claims.Claim("bot_as_user_id", botInfo.BotAsUserId.ToString()),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, botInfo.BotAsUserId.ToString()),
            new System.Security.Claims.Claim("bot_name", botInfo.BotName),
            new System.Security.Claims.Claim("is_verified", botInfo.IsVerified.ToString()),
            new System.Security.Claims.Claim("typ", "bot"),
        };

        var identity  = new System.Security.Claims.ClaimsIdentity(claims, SchemeName);
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private async Task<BotAuthCacheEntry?> ResolveBotFromToken(string token)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var bot = await db.BotEntities
           .AsNoTracking()
           .Where(b => b.BotToken == token)
           .Select(b => new BotAuthCacheEntry
            {
                AppId       = b.AppId,
                TeamId      = b.TeamId,
                BotAsUserId = b.BotAsUserId,
                BotName     = b.Name,
                IsRestricted = b.IsRestricted,
                IsVerified   = b.IsVerified,
                MaxSpaces   = b.MaxSpaces,
            })
           .FirstOrDefaultAsync();

        return bot;
    }

    private static string ComputeTokenHash(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(hash);
    }
}

public sealed class BotAuthCacheEntry
{
    public Guid   AppId        { get; init; }
    public Guid   TeamId       { get; init; }
    public Guid   BotAsUserId  { get; init; }
    public string BotName      { get; init; } = "";
    public bool   IsRestricted { get; init; }
    public bool   IsVerified   { get; init; }
    public int    MaxSpaces    { get; init; }
}
