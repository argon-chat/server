namespace Argon.Services.Ion;

using ion.runtime;
using Features.Auth;
using Features.Jwt;
using Microsoft.Extensions.Caching.Hybrid;
using AllowAnonymousAttribute = ArgonContracts.AllowAnonymousAttribute;

public sealed class ArgonOrleansInterceptor : IIonInterceptor
{
    public Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        var section = RequestContext.AllowCallChainReentrancy();
        var ctx     = ArgonRequestContext.Current;

        if (ctx.UserId is not null)
            section.SetUserId(ctx.UserId!.Value);
        section.SetUserCountry(ctx.Region);
        section.SetUserIp(ctx.Ip);
        if (!string.IsNullOrEmpty(ctx.MachineId))
            section.SetUserMachineId(ctx.MachineId);
        if (ctx.SessionId is not null)
            section.SetUserSessionId(ctx.SessionId.Value);
        return next(context, ct);
    }
}

public sealed class ArgonTransactionInterceptor(TokenAuthorization validationParameters, ILogger<ArgonTransactionInterceptor> logger)
    : IIonInterceptor
{
    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        var httpAccessor = context.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var httpContext  = httpAccessor.HttpContext;

        if (httpContext is null)
            throw new InvalidOperationException("HttpContext is not available");

        var allowAnonymous             = context.MethodName.GetCustomAttribute<AllowAnonymousAttribute>() != null;
        var doNotRequireSessionContext = context.MethodName.GetCustomAttribute<DoNotRequireSessionContextAttribute>() is not null;

        // Per-IP throttle for the anonymous identity surface (login/register/reset). Reuses the
        // EmailOtpStrategy sliding-window pattern (INCR + EXPIRE on first hit) over the shared
        // Dragonfly cache; per-email throttling lives inside IdentityInteraction (args not visible
        // here). Fail-open on any cache error so a cache blip can never lock out all logins.
        if (allowAnonymous && context.InterfaceName == typeof(IIdentityInteraction))
            await EnforceAnonymousIpRateLimitAsync(context, httpContext, ct);

        Guid? user   = null;
        Guid? device = null;

        if (!allowAnonymous)
        {
            var authorized = await Authorize(httpContext);

            user   = authorized?.id;
            device = authorized?.deviceId;
        }

        if (!allowAnonymous && user is null)
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));

        // A barred machine stops being served here, not only at the next sign-in: a session opened
        // before the ban would otherwise keep working for the whole life of its access token, and a
        // ban that takes effect "eventually" is not what anyone means by banning a machine.
        //
        // Only bound sessions carry a device id, so this costs nothing for the rest — and for them
        // there is nothing to check, since the server cannot tell which machine is asking.
        if (device is { } machine &&
            await IsDeviceBannedAsync(context.ServiceProvider, machine, ct))
            throw new IonRequestException(new IonProtocolError("DEVICE_BANNED", "Device is not allowed"));

        var severity = LockdownSeverity.Low;
        if (user is not null)
            severity = await ResolveLockdownSeverityAsync(context.ServiceProvider, user.Value, ct);

        if (doNotRequireSessionContext)
            SafeSetRequestContext(context, httpContext, user, severity);
        else
            SetRequestContext(context, httpContext, user, severity);

        // A session the user ended from another device must stop being honoured here, not merely lose
        // its transport: the refresh token it was issued with is stateless and outlives any access
        // token, so without this check GetMyAuthorization would keep re-minting for a session that was
        // revoked. Placed after the context is set because the sid comes out of the same cookie.
        if (user is not null &&
            ArgonRequestContext.Current.SessionId is { } sessionId &&
            await IsSessionRevokedAsync(context.ServiceProvider, user.Value, sessionId, ct))
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));

        // Record the user's current app locale (normalized to BCP-47) for this session, so the Bot API
        // can surface it on BotUserV1. Ephemeral & best-effort — never blocks or fails the request.
        if (user is not null)
        {
            var locale = Argon.Features.BotApi.LocaleNormalizer.ToBcp47(httpContext.GetClientLocale());
            if (locale is not null)
            {
                try
                {
                    await context.ServiceProvider
                       .GetRequiredService<Argon.Features.BotApi.UserLocaleRegistry>()
                       .Set(user.Value, locale);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to record user locale for {UserId}", user.Value);
                }
            }
        }

        await next(context, ct);
    }

    private void SafeSetRequestContext(IIonCallContext context, HttpContext httpContext, Guid? user, LockdownSeverity severity)
    {
        try
        {
            var data = new ArgonRequestContextData
            {
                Ip               = httpContext.GetIpAddress(),
                Region           = httpContext.GetRegion(),
                Ray              = httpContext.GetRay(),
                ClientName       = httpContext.GetClientName(),
                SessionId        = httpContext.TryGetSessionId(out var sessionId) ? sessionId : null,
                MachineId        = httpContext.TryGetMachineId(out var id) ? id : null,
                AppId            = httpContext.TryGetAppId(out var appId) ? appId : null,
                UserId           = user,
                Scope            = context.ServiceProvider,
                LockdownSeverity = severity,
            };

            ArgonRequestContext.Set(data);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Trying access to argon api, but incorrect configuration client");
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));
        }
    }

    private void SetRequestContext(IIonCallContext context, HttpContext httpContext, Guid? user, LockdownSeverity severity)
    {
        try
        {
            var data = new ArgonRequestContextData
            {
                Ip               = httpContext.GetIpAddress(),
                Region           = httpContext.GetRegion(),
                Ray              = httpContext.GetRay(),
                ClientName       = httpContext.GetClientName(),
                SessionId        = httpContext.GetSessionId(),
                MachineId        = httpContext.GetMachineId(),
                AppId            = httpContext.GetAppId(),
                UserId           = user,
                Scope            = context.ServiceProvider,
                LockdownSeverity = severity,
            };

            ArgonRequestContext.Set(data);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Trying access to argon api, but incorrect configuration client");
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));
        }
    }

    private async Task<TokenUserData?> Authorize(HttpContext httpContext)
    {
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var auth) || string.IsNullOrWhiteSpace(auth))
            throw new UnauthorizedAccessException("Authorization header missing");

        if (!auth.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Authorization header must be Bearer");

        var token = auth.ToString()["Bearer ".Length..].Trim();

        var authResult = await validationParameters.AuthorizeByToken(token, httpContext.GetMachineId());

        if (authResult.IsSuccess)
            return authResult.Value;
        return null;
    }

    private static readonly HybridCacheEntryOptions BannedDeviceCacheOptions = new()
    {
        Expiration           = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(10),
    };

    /// <summary>
    /// Whether this machine is barred. Cached, because the answer is "no" for everyone but a handful.
    /// </summary>
    /// <remarks>
    /// Fails <em>open</em>, consistently with the other cache gates on this path: a database
    /// incident must not lock every bound session out of the product. The blast radius is that a
    /// banned machine keeps working until the store answers again, which is the same trade the
    /// revocation gate above makes.
    /// </remarks>
    private static async Task<bool> IsDeviceBannedAsync(IServiceProvider sp, Guid deviceId, CancellationToken ct)
    {
        try
        {
            return await sp.GetRequiredService<HybridCache>().GetOrCreateAsync(
                $"device:banned:{deviceId}",
                async token =>
                {
                    await using var ctx = await sp
                       .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                       .CreateDbContextAsync(token);

                    var now = DateTimeOffset.UtcNow;

                    return await ctx.DeviceBans.AnyAsync(
                        x => x.DeviceId == deviceId && (x.ExpiresAt == null || x.ExpiresAt > now), token);
                },
                BannedDeviceCacheOptions,
                cancellationToken: ct);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly HybridCacheEntryOptions LockdownCacheOptions = new()
    {
        Expiration      = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(10),
    };

    // Shorter than the lockdown window above, and for the opposite reason: lockdown is a moderation
    // decision that can wait half a minute to take effect, while a revocation is a user watching a
    // screen and expecting the other device to fall off it. Still cached, because the answer is "no"
    // for essentially every request ever made and one Redis EXISTS per call is not worth paying.
    private static readonly HybridCacheEntryOptions RevokedSessionCacheOptions = new()
    {
        Expiration           = TimeSpan.FromSeconds(15),
        LocalCacheExpiration = TimeSpan.FromSeconds(5),
    };

    private static async Task<bool> IsSessionRevokedAsync(
        IServiceProvider sp, Guid userId, Guid sessionId, CancellationToken ct)
    {
        var key = SessionRevocation.RevokedKey(userId);

        try
        {
            // The whole set is fetched and cached per user rather than probing one member per
            // request: it is a handful of ids, and the alternative is a distinct cache entry for
            // every (user, session) pair that ever asks.
            var revoked = await sp.GetRequiredService<HybridCache>().GetOrCreateAsync(
                key,
                async token => await sp.GetRequiredService<IArgonCacheDatabase>().SetMembersAsync(key, token),
                RevokedSessionCacheOptions,
                cancellationToken: ct);

            if (revoked.Contains(sessionId.ToString()))
                return true;

            // And the pre-set key shape, for the same reason as in IdentityInteraction: a revocation
            // written before this deploy must not be forgotten by it.
            var legacy = SessionRevocation.LegacyRevokedKey(userId, sessionId);

            return await sp.GetRequiredService<HybridCache>().GetOrCreateAsync(
                legacy,
                async token => await sp.GetRequiredService<IArgonCacheDatabase>().KeyExistsAsync(legacy, token),
                BannedDeviceCacheOptions,
                cancellationToken: ct);
        }
        catch (Exception)
        {
            // Fail-open, consistently with every other cache gate on this path: a store incident must
            // not sign the whole instance out. The blast radius is that a revoked session survives
            // until the cache is answering again.
            return false;
        }
    }

    private static async Task<LockdownSeverity> ResolveLockdownSeverityAsync(
        IServiceProvider sp, Guid userId, CancellationToken ct)
    {
        var cache = sp.GetRequiredService<HybridCache>();

        var reason = await cache.GetOrCreateAsync(
            ArgonRequestContext.LockdownCacheKey(userId),
            async token =>
            {
                var dbFactory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await dbFactory.CreateDbContextAsync(token);
                return await db.Users
                   .AsNoTracking()
                   .Where(u => u.Id == userId)
                   .Select(u => u.LockdownReason)
                   .FirstOrDefaultAsync(token);
            },
            LockdownCacheOptions,
            cancellationToken: ct);

        return reason switch
        {
            LockdownReason.NONE                => LockdownSeverity.Low,
            LockdownReason.UNDER_INVESTIGATION => LockdownSeverity.Middle,
            LockdownReason.INCITING_MOMENT     => LockdownSeverity.Middle,
            _                                  => LockdownSeverity.Critical
        };
    }

    // Per-IP windows for the anonymous identity surface. Deliberately generous + short-windowed so
    // shared/CGNAT IPs and honest retypes are never locked out; the per-email limits (inside
    // IdentityInteraction) are the tighter, credential-specific guard. Tune freely / move to config.
    private static (int max, TimeSpan window)? AnonymousIpLimitFor(string methodName) => methodName switch
    {
        nameof(IIdentityInteraction.Authorize)                   => (100, TimeSpan.FromMinutes(5)),
        nameof(IIdentityInteraction.Registration)                => (20, TimeSpan.FromMinutes(10)),
        nameof(IIdentityInteraction.BeginResetPassword)          => (15, TimeSpan.FromMinutes(15)),
        nameof(IIdentityInteraction.ResetPassword)               => (30, TimeSpan.FromMinutes(10)),
        nameof(IIdentityInteraction.GetAuthorizationScenarioFor) => (60, TimeSpan.FromMinutes(5)),
        // GetAuthorizationScenario / GetMyAuthorization are not credential-bearing; leave unthrottled.
        _                                                        => null
    };

    private async Task EnforceAnonymousIpRateLimitAsync(IIonCallContext context, HttpContext httpContext, CancellationToken ct)
    {
        var limit = AnonymousIpLimitFor(context.MethodName.Name);
        if (limit is null)
            return;

        var ip = httpContext.GetIpAddress();
        if (string.IsNullOrEmpty(ip) || ip == "unknown")
            return; // cannot attribute an IP -> fail-open, never lock out

        long count;
        try
        {
            var cache = context.ServiceProvider.GetRequiredService<IArgonCacheDatabase>();
            var key   = $"rl:auth:ip:{ip}:{context.MethodName.Name}";
            count = await cache.StringIncrementAsync(key, ct);
            if (count == 1)
                await cache.KeyExpireAsync(key, limit.Value.window, ct);
        }
        catch (Exception e)
        {
            // Fail-open: this gate sits in front of 100% of anonymous logins. A Dragonfly hiccup
            // (or the InMemory single-instance cache, which doesn't implement INCR) must NOT become
            // a total login outage. Allow the request and move on.
            logger.LogWarning(e, "Anonymous auth rate-limit cache call failed; allowing request (fail-open)");
            return;
        }

        if (count > limit.Value.max)
        {
            logger.LogWarning("Anonymous auth rate limit hit: method={Method} ip={Ip} count={Count}",
                context.MethodName.Name, ip, count);
            throw new IonRequestException(new IonProtocolError("RATE_LIMITED", "Too many attempts, please try again later"));
        }
    }
}