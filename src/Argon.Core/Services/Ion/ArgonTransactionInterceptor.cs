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
        // How the caller described itself, for the grains that record a login. Descriptions, not
        // credentials — see CallerContext for the one value on this list that is a security fact.
        section.SetUserAppId(ctx.AppId);
        section.SetUserClient(ctx.Client);
        section.SetUserCity(ctx.Location.City);
        return next(context, ct);
    }
}

/// <summary>What the request interceptor caches about an account's lockdown.</summary>
public sealed record LockdownSnapshot(LockdownReason Reason, DateTimeOffset? ExpiresAt);

public sealed class ArgonTransactionInterceptor(
    TokenAuthorization                  validationParameters,
    IOptions<AnonymousRateLimitOptions> anonymousLimits,
    ILogger<ArgonTransactionInterceptor> logger)
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

        Guid?           user       = null;
        Guid?           device     = null;
        Guid?           credential = null;
        DateTimeOffset? mintedAt   = null;

        if (!allowAnonymous)
        {
            var authorized = await Authorize(httpContext);

            user       = authorized?.Token.id;
            device     = authorized?.Token.deviceId;
            credential = authorized?.Credential.SessionId;
            mintedAt   = authorized?.Credential.MintedAt;
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

        // The half of the caller's identity they did not choose. Everything downstream reads the
        // session id out of the ArgonSecure cookie, which the client writes; this is the id the
        // server minted into the credential the request is actually authenticated by, and it is what
        // lets PickTicket stamp a hub ticket a rotated cookie cannot shake off. See
        // SessionRevocation's remarks for why both halves are needed.
        if (credential is { } credentialSessionId)
            ArgonRequestContext.Current.Props[SessionRevocation.CredentialSessionProperty] = credentialSessionId.ToString();

        // A session the user ended from another device must stop being honoured here, not merely lose
        // its transport: the refresh token it was issued with is stateless and outlives any access
        // token, so without this check GetMyAuthorization would keep re-minting for a session that was
        // revoked. Placed after the context is set because the sid comes out of the same cookie.
        //
        // Both ids, not just the cookie's. The cookie's is the caller's to write, so a signed-out
        // device that generates a fresh scid presented an id nobody had tombstoned and every Ion RPC
        // in the product went on answering it — reading messages, listing spaces, sending. The
        // credential id above came out of the signature on the token this request is authenticated
        // by, and SecurityGrain.EndSessionAsync tombstones it alongside the row on the screen.
        //
        // And the floor beside them, because neither id reaches a credential nobody registered under
        // either — which is every token a password change is aimed at. Without it the one Ion call
        // that matters most, PickTicket, kept minting hub tickets stamped with a current iat from a
        // week-old access token: the hub's floor check saw a fresh ticket, the session grain saw a
        // fresh SessionStartTime, and a sign-out-everywhere ended nothing but the refresh.
        if (user is not null &&
            await IsSessionRevokedAsync(
                context.ServiceProvider, user.Value, [ArgonRequestContext.Current.SessionId, credential], mintedAt, ct))
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
                Location         = httpContext.GetGeoLocation(),
                Ray              = httpContext.GetRay(),
                ClientName       = httpContext.GetClientName(),
                Client           = httpContext.GetClientDescriptor(),
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
                Location         = httpContext.GetGeoLocation(),
                Ray              = httpContext.GetRay(),
                ClientName       = httpContext.GetClientName(),
                Client           = httpContext.GetClientDescriptor(),
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

    /// <summary>What the request's bearer token established: who is calling, and under which credential.</summary>
    private sealed record AuthorizedCaller(TokenUserData Token, AccessTokenCredential Credential);

    /// <summary>
    /// The two things a validated access token says about the session behind it: which one, and when
    /// it was minted.
    /// </summary>
    /// <remarks>
    /// Both are needed by the same gate and both come out of the same parse, so they travel together
    /// rather than as two lookups over the same token — see <see cref="CredentialOf"/>.
    /// </remarks>
    private readonly record struct AccessTokenCredential(Guid? SessionId, DateTimeOffset? MintedAt);

    private async Task<AuthorizedCaller?> Authorize(HttpContext httpContext)
    {
        if (!httpContext.Request.Headers.TryGetValue("Authorization", out var auth) || string.IsNullOrWhiteSpace(auth))
            throw new UnauthorizedAccessException("Authorization header missing");

        if (!auth.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Authorization header must be Bearer");

        var token = auth.ToString()["Bearer ".Length..].Trim();

        var authResult = await validationParameters.AuthorizeByToken(token, httpContext.GetMachineId());

        if (!authResult.IsSuccess)
            return null;

        return new AuthorizedCaller(authResult.Value, CredentialOf(token));
    }

    /// <summary>The <c>sid</c> and the mint time of an access token that has just been validated.</summary>
    /// <remarks>
    /// <para>Read here rather than handed back by <c>TokenAuthorization</c>, which answers with a
    /// <c>TokenUserData</c> that has no room for either. The token string is the one
    /// <c>AuthorizeByToken</c> just verified — signature, audience, lifetime, machine binding — so
    /// reading two more claims off it costs a parse and proves nothing new; the safety comes from the
    /// call above having succeeded, and this must never be called before it does.</para>
    ///
    /// <para>The session id is null for a token minted before the claim existed, which is every access
    /// token still in flight from before this deploy and every one minted by the sign-in path rather
    /// than by a refresh. Callers therefore treat it as a bonus identity rather than a required one.</para>
    ///
    /// <para><b>The mint time is <c>nbf</c> as often as <c>iat</c>, and that is not sloppiness.</b>
    /// <c>ClassicJwtFlow.GenerateAccessToken</c> hands <c>JwtSecurityToken</c> a <c>notBefore</c> and
    /// no issued-at, and the token library writes <c>iat</c> only when it is given one — so an access
    /// token carries <c>nbf</c> = the moment it was minted and no <c>iat</c> at all, while the refresh
    /// token and the hub ticket, which are compared against the same floor, write <c>iat</c>
    /// explicitly. Reading only <c>iat</c> here would place every access token ever minted as
    /// "undatable", which <see cref="SessionRevocation.IsBelowFloor"/> reads as older than any floor:
    /// one password change would lock the account out of the whole Ion surface permanently, including
    /// the tokens it signs in with afterwards. The smaller of the two is taken for the same reason the
    /// hub takes the smallest <c>iat</c> — the oldest reading is the conservative one against a
    /// watermark, and it keeps working unchanged on the day the mint starts writing <c>iat</c>.</para>
    /// </remarks>
    private AccessTokenCredential CredentialOf(string token)
    {
        try
        {
            var claims = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(token).Claims;

            Guid? session = null;
            long? minted  = null;

            foreach (var claim in claims)
            {
                switch (claim.Type)
                {
                    case "sid" when Guid.TryParse(claim.Value, out var parsed):
                        session = parsed;
                        break;
                    case "iat" or "nbf" when long.TryParse(claim.Value, out var seconds):
                        if (minted is null || seconds < minted)
                            minted = seconds;
                        break;
                }
            }

            return new AccessTokenCredential(
                session, minted is { } value ? DateTimeOffset.FromUnixTimeSeconds(value) : null);
        }
        catch (Exception e)
        {
            // A token that validated but will not re-parse is a contradiction worth a line in the
            // log; it is not a reason to refuse a request the validator already accepted. It does
            // lose against a floor, because a credential nobody can place in time is treated as older
            // than any watermark — the same reading the refresh path and the hub take.
            logger.LogWarning(e, "Could not read the credential session id off an already-validated access token");
            return default;
        }
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

    /// <param name="sessionIds">
    /// Every id this caller can be recognised by — the presence sid out of the cookie and the
    /// credential sid out of the token, either of which may be absent. Nulls are skipped, and a
    /// caller with none of them can still be refused by the floor, which is the point of the floor.
    /// </param>
    /// <param name="mintedAt">
    /// When the access token this request is authenticated by was minted, for the comparison against
    /// the user's sign-out-everywhere watermark. Null means "cannot be placed in time", which
    /// <see cref="SessionRevocation.IsBelowFloor"/> reads as older than any floor.
    /// </param>
    private static async Task<bool> IsSessionRevokedAsync(
        IServiceProvider sp, Guid userId, IReadOnlyList<Guid?> sessionIds, DateTimeOffset? mintedAt, CancellationToken ct)
    {
        var identities = sessionIds.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();

        var key = SessionRevocation.RevokedKey(userId);

        try
        {
            // The targeted half first: it answers "this device was signed out" for the overwhelming
            // majority of the revocations anyone actually performs, and it is one cache entry.
            if (identities.Count > 0)
            {
                // The whole set is fetched and cached per user rather than probing one member per
                // request: it is a handful of ids, and the alternative is a distinct cache entry for
                // every (user, session) pair that ever asks.
                var revoked = await sp.GetRequiredService<HybridCache>().GetOrCreateAsync(
                    key,
                    async token => await sp.GetRequiredService<IArgonCacheDatabase>().SetMembersAsync(key, token),
                    RevokedSessionCacheOptions,
                    cancellationToken: ct);

                if (identities.Any(id => revoked.Contains(id.ToString())))
                    return true;

                // And the pre-set key shape, for the same reason as in IdentityInteraction: a revocation
                // written before this deploy must not be forgotten by it.
                foreach (var id in identities)
                {
                    var legacy = SessionRevocation.LegacyRevokedKey(userId, id);

                    if (await sp.GetRequiredService<HybridCache>().GetOrCreateAsync(
                            legacy,
                            async token => await sp.GetRequiredService<IArgonCacheDatabase>().KeyExistsAsync(legacy, token),
                            BannedDeviceCacheOptions,
                            cancellationToken: ct))
                        return true;
                }
            }

            // The backstop, and it runs whether or not this caller had an id to look up: a password
            // change writes no per-session tombstone at all (SecurityGrain.ChangePasswordAsync), so
            // the floor is the only thing standing between a sign-out-everywhere and an access token
            // that is good for another week.
            return SessionRevocation.IsBelowFloor(await FloorAsync(sp, userId, ct), mintedAt);
        }
        catch (Exception)
        {
            // Fail-open, consistently with every other cache gate on this path: a store incident must
            // not sign the whole instance out. The blast radius is that a revoked session survives
            // until the cache is answering again.
            return false;
        }
    }

    /// <summary>The user's sign-out-everywhere watermark, or null.</summary>
    /// <remarks>
    /// Cached under its own key with the revoked set's lifetime, exactly as <c>AppHub</c> reads it:
    /// the two have different shapes and the same staleness budget, and one entry per user per kind
    /// is cheaper than a distinct entry for every pair that ever asks. Fifteen seconds of staleness is
    /// the same trade the tombstone above makes — a sign-out takes effect, not necessarily on the very
    /// next packet — and unlike the hub there is no once-only moment here to read uncached for.
    ///
    /// <para>Empty string rather than null, because a cache entry that holds nothing is
    /// indistinguishable from a miss and would put a Redis GET on every authenticated RPC.</para>
    /// </remarks>
    private static async Task<DateTimeOffset?> FloorAsync(IServiceProvider sp, Guid userId, CancellationToken ct)
    {
        var key = SessionRevocation.FloorKey(userId);

        var raw = await sp.GetRequiredService<HybridCache>().GetOrCreateAsync(
            key,
            async token => await sp.GetRequiredService<IArgonCacheDatabase>().StringGetAsync(key, token) ?? "",
            RevokedSessionCacheOptions,
            cancellationToken: ct);

        return SessionRevocation.ParseFloor(raw);
    }

    private static async Task<LockdownSeverity> ResolveLockdownSeverityAsync(
        IServiceProvider sp, Guid userId, CancellationToken ct)
    {
        var cache = sp.GetRequiredService<HybridCache>();

        // Reason and expiry together: a timed lockdown ends when its expiry passes, and nothing
        // else clears the column. Reading the reason alone made every timed ban permanent.
        var snapshot = await cache.GetOrCreateAsync(
            ArgonRequestContext.LockdownCacheKey(userId),
            async token =>
            {
                var dbFactory = sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
                await using var db = await dbFactory.CreateDbContextAsync(token);
                return await db.Users
                   .AsNoTracking()
                   .Where(u => u.Id == userId)
                   .Select(u => new LockdownSnapshot(u.LockdownReason, u.LockDownExpiration))
                   .FirstOrDefaultAsync(token) ?? new LockdownSnapshot(LockdownReason.NONE, null);
            },
            LockdownCacheOptions,
            cancellationToken: ct);

        if (snapshot.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
            return LockdownSeverity.Low;

        return Argon.Features.Moderation.ReportActionPlanner.SeverityOf(snapshot.Reason);
    }


    private async Task EnforceAnonymousIpRateLimitAsync(IIonCallContext context, HttpContext httpContext, CancellationToken ct)
    {
        var options = anonymousLimits.Value;

        if (!options.Enabled)
            return;

        var limit = options.For(context.MethodName.Name);
        if (limit is null)
            return;   // not credential-bearing; left open on purpose

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
                await cache.KeyExpireAsync(key, limit.Window, ct);
        }
        catch (Exception e)
        {
            // Fail-open: this gate sits in front of 100% of anonymous logins. A Dragonfly hiccup
            // (or the InMemory single-instance cache, which doesn't implement INCR) must NOT become
            // a total login outage. Allow the request and move on.
            logger.LogWarning(e, "Anonymous auth rate-limit cache call failed; allowing request (fail-open)");
            return;
        }

        if (count > limit.Max)
        {
            logger.LogWarning("Anonymous auth rate limit hit: method={Method} ip={Ip} count={Count}",
                context.MethodName.Name, ip, count);
            throw new IonRequestException(new IonProtocolError("RATE_LIMITED", "Too many attempts, please try again later"));
        }
    }
}