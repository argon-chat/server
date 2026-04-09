namespace Argon.Features.BotApi;

using Argon.Grains.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Hybrid;

public sealed class BotTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory                              logger,
    System.Text.Encodings.Web.UrlEncoder        encoder,
    IClusterClient                              clusterClient,
    HybridCache                                 cache)
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

        var cacheKey = $"bot:auth:{ComputeTokenHash(token)}";
        var botInfo = await cache.GetOrCreateAsync(
            cacheKey,
            token,
            async (t, ct) =>
            {
                var grain = clusterClient.GetGrain<IBotDirectoryGrain>(Guid.Empty);
                return await grain.ResolveByToken(t);
            },
            new HybridCacheEntryOptions
            {
                Expiration           = TimeSpan.FromMinutes(5),
                LocalCacheExpiration = TimeSpan.FromMinutes(1)
            });

        if (botInfo is null)
            return AuthenticateResult.Fail("Invalid bot token");

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

    private static string ComputeTokenHash(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(hash);
    }
}
