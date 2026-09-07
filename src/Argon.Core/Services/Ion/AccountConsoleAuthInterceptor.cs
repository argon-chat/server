namespace Argon.Services.Ion;

using Features.AccountConsole;
using Features.Auth;
using ion.runtime;
using System.Security.Claims;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Ion interceptor for the developer account console. Validates the caller's token against the
/// OAuth provider's JWKS and publishes the request context the console's services read.
/// </summary>
/// <remarks>
/// <para>The key set is fetched and refreshed by <see cref="ConfigurationManager{T}"/> — the same
/// machinery <see cref="OperatorAuthInterceptor"/> uses — rather than by a hand-rolled cache with a
/// fixed lifetime, so a rotated signing key is picked up instead of failing every request for a
/// day.</para>
///
/// <para><b>A valid signature is not the whole question, and this door used to think it was.</b>
/// Defect R6: an erasure's sign-out-everywhere reaches the Ion interceptor, the refresh path and the
/// hub, because all three test the user's revocation floor — and it did not reach here, so a console
/// token minted before an account was erased went on authenticating against <c>IAccountConsole</c>,
/// <c>ITeamConsole</c> and <c>IAppManagement</c> for its whole lifetime. It was the one credential the
/// erasure could not revoke. Two gates close that, and they are the same two the first-party path
/// applies: the floor, and the existence of an account behind the subject.</para>
/// </remarks>
public sealed class AccountConsoleAuthInterceptor(
    ILogger<AccountConsoleAuthInterceptor> logger,
    IOptions<AccountConsoleAuthOptions> options)
    : IIonInterceptor
{
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> configManager = new(() =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            options.Value.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever()));

    private static readonly JsonWebTokenHandler TokenHandler = new();

    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.MetadataAddress) || string.IsNullOrWhiteSpace(options.Value.ValidIssuer))
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Account console auth is not configured"));

        var accessor    = context.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var httpContext = accessor.HttpContext ?? throw new InvalidOperationException("HttpContext is not available");

        var token = ExtractBearerToken(httpContext);

        if (token is null)
        {
            logger.LogWarning("No authorization header was supplied, returning NO_AUTH");
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));
        }

        var configuration = await configManager.Value.GetConfigurationAsync(ct);

        var result = await TokenHandler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer              = options.Value.ValidIssuer,
            ValidAudiences           = options.Value.ValidAudiences,
            ValidateAudience         = options.Value.ValidAudiences.Count > 0,
            IssuerSigningKeys        = configuration.SigningKeys,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true
        });

        if (!result.IsValid)
        {
            logger.LogWarning("Invalid console token from IP={Ip}: {Error}",
                httpContext.Connection.RemoteIpAddress, result.Exception?.Message);
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Invalid or expired token"));
        }

        var claims = result.ClaimsIdentity;

        if (claims.FindFirst("sub")?.Value is not { } subject || !Guid.TryParse(subject, out var userId))
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Token carries no usable subject"));

        // The sign-out-everywhere watermark. Everything this account was issued at or before it is dead,
        // and an erasure writes it (AccountDeletionGrain.InvalidateSessionsAsync) exactly so that every
        // credential of the erased account stops working at once — this one included.
        if (await IsBelowFloorAsync(context.ServiceProvider, userId, MintedAt(result, claims), ct))
        {
            logger.LogWarning("Console token for {UserId} is below the account's revocation floor", userId);

            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Session has been revoked"));
        }

        // And an account has to exist behind the subject. The console reads its display fields from the
        // token's own claims, so every read here succeeds against a user row that is gone — an erased
        // account kept rendering its own console page, and a subject that never had an account at all
        // (CON-5) was served the same way.
        if (!await AccountExistsAsync(context.ServiceProvider, userId, ct))
        {
            logger.LogWarning("Console token for {UserId} names no live account", userId);

            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Account no longer exists"));
        }

        var headers = context.RequestItems;

        string Header(string name, string fallback)
            => headers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

        ArgonRequestContext.Set(new ArgonRequestContextData
        {
            Ip         = Header("CF-Connecting-IP", "unknown"),
            Region     = Header("CF-IPCountry", "unknown"),
            Ray        = Header("CF-Ray", ArgonId.New().ToString()),
            ClientName = Header("User-Agent", "unknown"),
            SessionId  = default,
            MachineId  = default,
            AppId      = default,
            UserId     = userId,
            Scope      = context.ServiceProvider,
            Props =
            {
                ["displayName"] = claims.FindFirst("displayName")?.Value ?? "Unknown User",
                ["avatarId"]    = claims.FindFirst("avatarFileId")?.Value ?? ""
            }
        });

        await next(context, ct);
    }

    /// <summary>
    /// When the console token was minted, for the comparison against the account's revocation floor.
    /// </summary>
    /// <remarks>
    /// <c>iat</c> and <c>nbf</c> both, smallest wins, exactly as <c>ArgonTransactionInterceptor</c> reads
    /// an access token: which of the two a mint writes is a decision of whatever library signed it, and
    /// the oldest reading is the conservative one against a watermark. Null when the token carries
    /// neither, which <see cref="SessionRevocation.IsBelowFloor"/> treats as older than any floor — a
    /// credential nobody can place in time cannot be shown to be newer than a sign-out-everywhere.
    /// </remarks>
    private static DateTimeOffset? MintedAt(TokenValidationResult result, ClaimsIdentity claims)
    {
        long? minted = null;

        foreach (var name in (ReadOnlySpan<string>)["iat", "nbf"])
        {
            if (claims.FindFirst(name)?.Value is { } raw && long.TryParse(raw, out var seconds) &&
                (minted is null || seconds < minted))
                minted = seconds;
        }

        if (minted is { } value)
            return DateTimeOffset.FromUnixTimeSeconds(value);

        // The parsed token's own reading, for a mint that wrote the claims in a shape the strings above
        // do not cover. Default(DateTime) means the token carried neither.
        return result.SecurityToken is JsonWebToken jwt && jwt.ValidFrom != default
            ? new DateTimeOffset(jwt.ValidFrom, TimeSpan.Zero)
            : null;
    }

    /// <summary>Whether this token predates the account's sign-out-everywhere watermark.</summary>
    /// <remarks>
    /// Fails <em>closed</em>, unlike the same gate on the first-party Ion path, and the difference is
    /// what each surface costs. There, refusing on a cache error would sign every client of the instance
    /// out of the product during a Redis incident; here it turns a developer console tab into a retry.
    /// This is also the only gate standing between an erased account and its own console, so the
    /// direction to fail in is the one that keeps the erasure's promise.
    /// </remarks>
    private async Task<bool> IsBelowFloorAsync(IServiceProvider sp, Guid userId, DateTimeOffset? mintedAt, CancellationToken ct)
    {
        var key = SessionRevocation.FloorKey(userId);

        try
        {
            var raw = await sp.GetRequiredService<HybridCache>().GetOrCreateAsync(
                key,
                async token => await sp.GetRequiredService<IArgonCacheDatabase>().StringGetAsync(key, token) ?? "",
                SessionRevocation.CacheOptions,
                cancellationToken: ct);

            return SessionRevocation.IsBelowFloor(SessionRevocation.ParseFloor(raw), mintedAt);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not read the revocation floor of {UserId}; refusing the console call", userId);

            return true;
        }
    }

    /// <summary>Whether a live, non-erased account exists for this subject.</summary>
    /// <remarks>
    /// <para>Read under the global soft-delete filter, so an anonymised row answers "no" without this
    /// having to know how erasure marks one.</para>
    ///
    /// <para>Cached for the same reason every other per-request account read on the Ion path is: the
    /// answer is "yes" for essentially every call ever made. Half a minute of staleness is the same
    /// budget <c>ArgonTransactionInterceptor</c> gives a lockdown, and the floor above is the gate that
    /// takes effect immediately.</para>
    ///
    /// <para>Fails <em>open</em>, where the floor fails closed: a database blip locking every developer
    /// out of their own console is a worse answer than half a minute of an account that no longer exists
    /// reading its own status page, and the floor has already refused the erased case.</para>
    /// </remarks>
    private async Task<bool> AccountExistsAsync(IServiceProvider sp, Guid userId, CancellationToken ct)
    {
        try
        {
            return await sp.GetRequiredService<HybridCache>().GetOrCreateAsync(
                $"console:account:{userId}",
                async token =>
                {
                    await using var db = await sp
                       .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                       .CreateDbContextAsync(token);

                    return await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId, token);
                },
                AccountCacheOptions,
                cancellationToken: ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not check whether {UserId} still has an account; allowing the console call", userId);

            return true;
        }
    }

    private static readonly HybridCacheEntryOptions AccountCacheOptions = new()
    {
        Expiration           = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(10)
    };

    private static string? ExtractBearerToken(HttpContext httpContext)
    {
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var auth) || string.IsNullOrWhiteSpace(auth))
            return null;

        var value = auth.ToString();

        return value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value["Bearer ".Length..].Trim()
            : value.Trim();
    }
}
