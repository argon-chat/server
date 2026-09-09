namespace Argon.Features.Invites;

using Argon.Features.Discovery;
using Argon.Features.Storage;
using Argon.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>
/// The invite card an invite link needs before anyone is signed in.
/// </summary>
/// <remarks>
/// <para><c>UserInteraction.PreviewInvite</c> answers the same question over Ion, but only for a
/// caller who already holds a session — which the landing page at <c>argon.gl/i/{code}</c>, and the
/// crawler unfurling that link into a chat, never do. Rather than let the landing render an
/// anonymous "somebody invited you somewhere" box, the same preview is served here as plain JSON,
/// anonymously, from the one place that can resolve a code.</para>
///
/// <para>It gives away nothing the link does not already give away: handing someone the code is
/// exactly the act of letting them see the space and walk into it. Enumeration is answered by the
/// size of the code space and by a per-address throttle that fails OPEN, because a cache outage must
/// never take invite links off the internet.</para>
/// </remarks>
public static class InviteCardEndpoint
{
    public const string RoutePath = "/api/invite/{code}";

    /// <summary>How many card lookups one address may make inside <see cref="Window"/>.</summary>
    private const int RequestsPerWindow = 60;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>
    /// A day, because an invite row is written once and then only ever deleted.
    /// </summary>
    /// <remarks>
    /// <para>A posted link is read once per person who sees the message, plus once per chat client
    /// that unfurls it: a burst of identical reads, none of them from anyone with an account, all
    /// asking the same question about the same code. A miss costs an invite lookup plus a space
    /// grain call that walks the whole member roster to count who is online. Nothing about the
    /// answer is a decision — joining goes through <c>JoinToSpace</c>, which re-reads the invite and
    /// refuses it if it has stopped being good — so the only question is how long is honest.</para>
    ///
    /// <para>The entry is keyed by code, and a code's meaning cannot change: <c>SpaceInvite</c> is
    /// inserted, its <c>UsedCount</c> ticks, and it is deleted. Deletion is the single act that can
    /// falsify this entry, and it drops it (see <see cref="InviteCardCache"/>). Expiry is the second
    /// way an invite stops working, and it is not an event anyone raises — so it is checked against
    /// the clock on the way out instead, from the timestamp the record carries. What is left riding
    /// along is the space's name, picture and counts: they can be up to a day old here, they are
    /// decoration on a card that leads somewhere authoritative, and paying a roster walk per reader
    /// to keep an online count fresh on a public landing page would be a poor trade.</para>
    ///
    /// <para>The in-process copy is the short one. Revoking reaches the shared entry, but nothing
    /// can reach into another process's memory, so this is the real ceiling on how long a revoked
    /// link keeps being advertised — five minutes, chosen rather than inherited.</para>
    /// </remarks>
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration           = TimeSpan.FromDays(1),
        LocalCacheExpiration = TimeSpan.FromMinutes(5)
    };

    /// <summary>
    /// A refusal is not held for a day.
    /// </summary>
    /// <remarks>
    /// Caching them at all is worth it — an expired link is pasted around exactly as much as a live
    /// one, and a mistyped code must not reach a grain once per curious reader. Holding them as long
    /// as a real card is not: a refusal is the one answer an attacker can mint at will, one entry
    /// per code they try, and there is no reason to keep the residue of that for twenty-four hours.
    /// </remarks>
    private static readonly HybridCacheEntryOptions RefusalOptions = new()
    {
        Expiration           = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromMinutes(1)
    };

    public static WebApplication MapInviteCard(this WebApplication app)
    {
        app.MapGet(RoutePath, InviteCardHandler)
           .AllowAnonymous().RequireCors(DiscoveryFeature.OpenPublicPolicy);

        return app;
    }

    private static async Task<IResult> InviteCardHandler(
        HttpContext              ctx,
        string                   code,
        IClusterClient           cluster,
        IArgonRegionRegistry     registry,
        IOptions<StorageOptions> storage,
        IArgonCacheDatabase      cache,
        HybridCache              hybrid,
        ILoggerFactory           loggerFactory,
        CancellationToken        ct)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 32)
            return Failure(AcceptInviteError.NOT_FOUND);

        if (await IsThrottled(cache, ctx, ct))
            return Results.Json(new InviteCardErrorDto("RATE_LIMITED"), statusCode: StatusCodes.Status429TooManyRequests);

        var            key   = InviteCardCache.KeyOf(code);
        var            fresh = false;
        ResolvedInvite resolved;

        try
        {
            resolved = await hybrid.GetOrCreateAsync(
                key,
                (cluster, registry, code),
                async (state, token) =>
                {
                    fresh = true;
                    return await ResolveAsync(state.cluster, state.registry, state.code, token);
                },
                CacheOptions,
                cancellationToken: ct);

            // Written back only on the read that produced it, so a refused code costs one extra
            // write in its lifetime rather than one per reader.
            if (fresh && resolved.Error != AcceptInviteError.NONE)
                await hybrid.SetAsync(key, resolved, RefusalOptions, cancellationToken: ct);
        }
        catch (Exception e)
        {
            // Deliberately not cached: an unreachable region or a failing grain is a state of this
            // deployment, not a fact about the invite, and pinning it would turn a blip into a day
            // of every link being broken.
            loggerFactory.CreateLogger("InviteCard").LogWarning(e, "invite card lookup failed for {Code}", code);
            return Results.Json(new InviteCardErrorDto("INTERNAL_ERROR"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (resolved.Error != AcceptInviteError.NONE)
            return Failure(resolved.Error);

        // The one way a cached card goes bad on its own. Nobody raises an event when an invite's
        // hour arrives, so the record carries its own deadline and is checked against the clock
        // here; dropping it keeps the next reader from paying for the same discovery.
        if (resolved.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await InviteCardCache.InvalidateAsync(hybrid, code, ct);
            return Failure(AcceptInviteError.EXPIRED);
        }

        // A minute, not the day the entry behind it gets, and the difference is on purpose: a
        // revoked invite is dropped from a cache this process owns, and nothing can reach into a
        // CDN's copy or a browser's. Whatever is cached out there is cached past every act that
        // could correct it, so it is kept to the burst it exists to absorb.
        ctx.Response.Headers.CacheControl = "public, max-age=60, stale-while-revalidate=300";

        // Built per request, not cached: a file URL is only absolute relative to the host that was
        // asked (see FileBase), and a cached one would hand one host's origin to another's readers.
        var fileBase = FileBase(ctx, storage.Value.Cdn);

        return Results.Ok(new InviteCardDto(
            Code: code,
            Kind: resolved.VoiceChannelId is null ? "space" : "voice",
            DeepLink: $"argon://invite/{Uri.EscapeDataString(code)}",
            Space: new InviteCardSpaceDto(
                Id: resolved.SpaceId,
                Name: resolved.Name,
                Description: resolved.Description,
                AvatarUrl: FileUrl(fileBase, resolved.AvatarFileId),
                BannerUrl: FileUrl(fileBase, resolved.BannerFileId),
                SplashUrl: FileUrl(fileBase, resolved.SplashFileId),
                IsVerified: resolved.IsVerified,
                IsOfficial: resolved.IsOfficial,
                IsCommunity: resolved.IsCommunity,
                MemberCount: resolved.MemberCount,
                OnlineCount: resolved.OnlineCount),
            VoiceChannel: resolved.VoiceChannelId is { } voiceId
                ? new InviteCardVoiceDto(voiceId, resolved.VoiceChannelName ?? "")
                : null));
    }

    /// <summary>
    /// The half of the answer that is the same for every reader: what the code points at, and what
    /// that space looks like. File ids rather than URLs, because a URL depends on who is asking.
    /// </summary>
    private static async ValueTask<ResolvedInvite> ResolveAsync(
        IClusterClient cluster, IArgonRegionRegistry registry, string code, CancellationToken ct)
    {
        // String-keyed, so it resolves in this region — the same path Ion takes, and the reason
        // invite rows are placed regionally rather than replicated.
        var (target, error) = await cluster.GetGrain<IInviteGrain>(code).PreviewAsync();

        if (error != AcceptInviteError.NONE || target is null)
            return ResolvedInvite.Failed(error == AcceptInviteError.NONE ? AcceptInviteError.NOT_FOUND : error);

        var preview = await SpaceGrainOf(cluster, registry, target.SpaceId).GetInvitePreview();

        return new ResolvedInvite(
            AcceptInviteError.NONE,
            target.ExpiresAt,
            preview.spaceId,
            preview.name,
            preview.description,
            preview.avatarFileId,
            preview.topBannerFileId,
            preview.inviteImageFileId,
            preview.isVerified,
            preview.isOfficial,
            preview.isCommunity ?? false,
            preview.memberCount,
            preview.onlineCount,
            target.VoiceChannelId,
            target.VoiceChannelName);
    }

    /// <summary>
    /// The space grain, from whichever region owns the space.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>ServiceEx.GetGrain</c>, which every Ion call already goes through: a local id never
    /// touches the registry, and a foreign id whose owner is unusable throws instead of quietly
    /// falling back to this region's database.
    /// </remarks>
    private static ISpaceGrain SpaceGrainOf(IClusterClient cluster, IArgonRegionRegistry registry, Guid spaceId)
    {
        if (!ForeignRegionCalls.IsForeign(spaceId))
            return cluster.GetGrain<ISpaceGrain>(spaceId);

        if (registry.TryGetClientFor(spaceId, out var owner))
            return owner.GetGrain<ISpaceGrain>(spaceId);

        return registry.GetClient(registry.RegionOf(spaceId)).GetGrain<ISpaceGrain>(spaceId);
    }

    /// <summary>Per-address throttle; fails open, so a cache outage cannot break invite links.</summary>
    private static async Task<bool> IsThrottled(IArgonCacheDatabase cache, HttpContext ctx, CancellationToken ct)
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString();

        if (string.IsNullOrEmpty(ip))
            return false;

        try
        {
            var key   = $"rl:invite:card:{ip}";
            var count = await cache.StringIncrementAsync(key, ct);

            if (count == 1)
                await cache.KeyExpireAsync(key, Window, ct);

            return count > RequestsPerWindow;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Where files live, as seen by whoever is reading this card.
    /// </summary>
    /// <remarks>
    /// The configured public base when there is one, otherwise this request's own origin: the card is
    /// read cross-origin by the landing page, where a relative <c>/files/…</c> would resolve against
    /// <c>argon.gl</c> rather than the API. Self-hosters who never set
    /// <c>Storage:Cdn:PublicBaseUrl</c> therefore still hand out image URLs that load.
    /// </remarks>
    private static string FileBase(HttpContext ctx, CdnOptions cdn)
        => string.IsNullOrWhiteSpace(cdn.PublicBaseUrl)
            ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
            : cdn.PublicBaseUrl;

    private static string? FileUrl(string baseUrl, string? fileId)
        => string.IsNullOrWhiteSpace(fileId) ? null : CdnOptions.FileUrlOn(baseUrl, fileId);

    private static IResult Failure(AcceptInviteError error) => error switch
    {
        // Gone rather than Not Found: the link was real, and a reader that can tell the two apart can
        // say "this invite has expired" instead of "this link is wrong".
        AcceptInviteError.EXPIRED       => Results.Json(new InviteCardErrorDto("EXPIRED"), statusCode: StatusCodes.Status410Gone),
        AcceptInviteError.LIMIT_REACHED => Results.Json(new InviteCardErrorDto("LIMIT_REACHED"), statusCode: StatusCodes.Status410Gone),
        _                               => Results.Json(new InviteCardErrorDto("NOT_FOUND"), statusCode: StatusCodes.Status404NotFound)
    };
}

/// <summary>
/// The cache the public invite card is served from, addressed from both sides of it.
/// </summary>
/// <remarks>
/// The key lives here rather than inside the endpoint because the write side needs it too: an
/// invite that has been revoked has to stop being advertised, and the only way to say that is to
/// name the same entry the endpoint reads. Two spellings of one key is how a cache ends up holding
/// something nobody can drop.
/// </remarks>
public static class InviteCardCache
{
    /// <summary>
    /// One key per code, however the code was written down.
    /// </summary>
    /// <remarks>
    /// The dashed display form and the bare form are the same invite — <c>TryParseInviteCode</c>
    /// strips separators before decoding — so they must not become two entries. Case is kept: the
    /// alphabet is base62, where <c>a</c> is not <c>A</c>.
    /// </remarks>
    public static string KeyOf(string code)
        => $"invite:card:{InviteCodeEntityData.RemoveSeparators(code)}";

    /// <summary>
    /// Drops a code's card, so a link that has just been revoked stops being advertised.
    /// </summary>
    /// <remarks>
    /// <para>Reaches the shared copy only. Every node's own in-process copy lives until its local
    /// expiry, which is why that expiry is minutes rather than the day the shared entry gets: the
    /// two numbers together are the honest answer to "how long can a revoked link still look alive
    /// on the web".</para>
    ///
    /// <para>Never allowed to fail the caller. Revoking an invite is a database delete that has
    /// already happened and already works — the link is dead either way, and a cache that cannot be
    /// reached must not turn a completed moderation action into an error.</para>
    /// </remarks>
    public static async Task InvalidateAsync(HybridCache cache, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;

        try
        {
            await cache.RemoveAsync(KeyOf(code), ct);
        }
        catch
        {
            // ignored — see remarks
        }
    }
}

/// <summary>
/// A resolved code on its way into the cache — the refusal, or the space and room it leads to.
/// </summary>
/// <remarks>
/// The refusal lives in the record rather than in a thrown exception so that one entry answers "what
/// does this code do", including when the answer is "nothing".
/// </remarks>
public sealed record ResolvedInvite(
    AcceptInviteError Error,
    /// <summary>When this stops being true on its own — the invite's own expiry, not the cache's.</summary>
    DateTimeOffset    ExpiresAt,
    Guid              SpaceId,
    string            Name,
    string            Description,
    string?           AvatarFileId,
    string?           BannerFileId,
    string?           SplashFileId,
    bool              IsVerified,
    bool              IsOfficial,
    bool              IsCommunity,
    int               MemberCount,
    int               OnlineCount,
    Guid?             VoiceChannelId,
    string?           VoiceChannelName)
{
    public static ResolvedInvite Failed(AcceptInviteError error)
        => new(error, DateTimeOffset.MaxValue, Guid.Empty, "", "", null, null, null, false, false, false, 0, 0, null, null);
}

public sealed record InviteCardDto(
    string              Code,
    string              Kind,
    string              DeepLink,
    InviteCardSpaceDto  Space,
    InviteCardVoiceDto? VoiceChannel);

public sealed record InviteCardSpaceDto(
    Guid    Id,
    string  Name,
    string  Description,
    string? AvatarUrl,
    string? BannerUrl,
    string? SplashUrl,
    bool    IsVerified,
    bool    IsOfficial,
    bool    IsCommunity,
    int     MemberCount,
    int     OnlineCount);

public sealed record InviteCardVoiceDto(Guid Id, string Name);

public sealed record InviteCardErrorDto(string Error);
