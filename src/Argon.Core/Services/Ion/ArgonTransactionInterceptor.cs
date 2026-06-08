namespace Argon.Services.Ion;

using ion.runtime;
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

        Guid? user = null;
        if (!allowAnonymous)
        {
            user = await Authorize(httpContext);
        }

        if (!allowAnonymous && user is null)
            throw new IonRequestException(new IonProtocolError("NO_AUTH", "Unauthorized"));

        var severity = LockdownSeverity.Low;
        if (user is not null)
            severity = await ResolveLockdownSeverityAsync(context.ServiceProvider, user.Value, ct);

        if (doNotRequireSessionContext)
            SafeSetRequestContext(context, httpContext, user, severity);
        else
            SetRequestContext(context, httpContext, user, severity);

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

    private async Task<Guid?> Authorize(HttpContext httpContext)
    {
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var auth) || string.IsNullOrWhiteSpace(auth))
            throw new UnauthorizedAccessException("Authorization header missing");

        if (!auth.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Authorization header must be Bearer");

        var token = auth.ToString()["Bearer ".Length..].Trim();

        var authResult = await validationParameters.AuthorizeByToken(token, httpContext.GetMachineId());

        if (authResult.IsSuccess)
            return authResult.Value.id;
        return null;
    }

    private static readonly HybridCacheEntryOptions LockdownCacheOptions = new()
    {
        Expiration      = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(10),
    };

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