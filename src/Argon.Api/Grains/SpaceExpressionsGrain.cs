namespace Argon.Grains;

using Argon.Core.Features.Transport;
using Argon.Core.Services;
using Argon.Features.Expressions;
using Argon.Features.Storage;
using Argon.Services;
using Argon.Services.L1L2;
using ion.runtime;
using Microsoft.Extensions.Caching.Hybrid;

/// <summary>
/// A space's sticker and custom emoji packs. The only writer of their rows, so the snapshot it keeps in
/// memory is always current; the shared cache entry only saves a new activation the query.
/// </summary>
public partial class SpaceExpressionsGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IEntitlementChecker entitlementChecker,
    IPermissionCache permissionCache,
    AppHubServer appHubServer,
    HybridCache cache,
    IArgonCacheDatabase counters,
    IS3StorageService s3,
    IExpressionFileValidator validator,
    IFirstFrameRenderer renderer,
    IReferenceCountService refCount,
    IOptions<ExpressionsOptions> options,
    ILogger<SpaceExpressionsGrain> logger) : Grain, ISpaceExpressionsGrain
{
    // No local copy: the activation is the local copy, and one on another silo could only go stale.
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromMinutes(10),
        Flags      = HybridCacheEntryFlags.DisableLocalCache
    };

    public static string CacheKey(Guid spaceId) => $"space:expressions:{spaceId}";

    private Guid               SpaceId => this.GetPrimaryKey();
    private ExpressionsOptions Limits  => options.Value;

    private Versioned<IonArray<ExpressionPack>>? snapshot;
    private Dictionary<Guid, ExpressionItem>     liveItems = new();

    // Reads interleave with writes; a read that started before a write must not put back what it saw.
    private int generation;

    public async Task<ExpressionsSnapshot> GetExpressions(string? known)
    {
        var callerId = this.GetUserId();

        if (await permissionCache.GetMemberWithArchetypesAsync(SpaceId, callerId) is null)
            throw new InvalidOperationException($"user '{callerId}' is not a member of space '{SpaceId}'");

        var current = await SnapshotAsync();

        return new ExpressionsSnapshot(current.Version,
            known == current.Version ? (IonArray<ExpressionPack>?)null : current.Value);
    }

    public async Task<ExpressionQuota> GetQuota()
    {
        var callerId = this.GetUserId();

        if (await permissionCache.GetMemberWithArchetypesAsync(SpaceId, callerId) is null)
            throw new InvalidOperationException($"user '{callerId}' is not a member of space '{SpaceId}'");

        var packs = (await SnapshotAsync()).Value.ToList();

        await using var ctx = await context.CreateDbContextAsync();
        var boost = await BoostLevelAsync(ctx);

        int Used(ExpressionKind kind) => packs.Where(p => p.kind == kind).Sum(p => p.items.Count);

        return new ExpressionQuota(packs.Count, Limits.PacksPerSpace,
            Used(ExpressionKind.Sticker), Limits.SlotsFor(ExpressionKind.Sticker, boost),
            Used(ExpressionKind.Emoji), Limits.SlotsFor(ExpressionKind.Emoji, boost),
            Limits.ItemsPerStickerPack, Limits.ItemsPerEmojiPack, boost);
    }

    public async Task<IReadOnlyDictionary<Guid, ExpressionItem>> ResolveLiveItemsAsync(IReadOnlyCollection<Guid> itemIds)
    {
        var found = new Dictionary<Guid, ExpressionItem>();
        if (itemIds.Count == 0)
            return found;

        await SnapshotAsync();

        foreach (var id in itemIds)
            if (liveItems.TryGetValue(id, out var item))
                found[id] = item;

        return found;
    }

    public async Task<IPackResult> CreatePack(ExpressionKind kind, string title, string slug)
    {
        var callerId = this.GetUserId();
        var rights   = await RightsAsync(callerId);

        if (!rights.MayCreate)
            return new FailedPack(ExpressionError.FORBIDDEN);

        if (!kind.IsKnown() || !ExpressionLimits.IsValidPackTitle(title) || !ExpressionLimits.IsValidPackSlug(slug))
            return new FailedPack(ExpressionError.INVALID_FORMAT);

        await using var ctx = await context.CreateDbContextAsync();

        var packs = await ctx.ExpressionPacks
           .AsNoTracking()
           .Where(p => p.SpaceId == SpaceId)
           .Select(p => new { p.Id, p.Kind, p.Slug, p.SortOrder, p.CreatorId })
           .ToListAsync();

        // A bot repeating its own create gets the pack it made.
        if (rights.Bot && packs.FirstOrDefault(p => p.Slug == slug) is { } same && same.CreatorId == callerId && same.Kind == kind
         && await PackAsync(same.Id) is { } existing)
            return new SuccessPack(existing);

        if (packs.Count >= Limits.PacksPerSpace)
            return new FailedPack(ExpressionError.QUOTA_EXCEEDED);
        if (packs.Any(p => p.Slug == slug))
            return new FailedPack(ExpressionError.NAME_TAKEN);

        if (!await TakeMutationAsync(rights.Bot))
            return new FailedPack(ExpressionError.RATE_LIMITED);

        var before = (await SnapshotAsync()).Version;

        var pack = new ExpressionPackEntity
        {
            Id        = ArgonId.New(),
            SpaceId   = SpaceId,
            Kind      = kind,
            Title     = title,
            Slug      = slug,
            CreatorId = callerId,
            SortOrder = packs.Where(p => p.Kind == kind).Select(p => p.SortOrder + 1).DefaultIfEmpty(0).Max(),
            Version   = 1
        };

        ctx.ExpressionPacks.Add(pack);

        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            return new FailedPack(ExpressionError.NAME_TAKEN);
        }

        var after = await RefreshAsync();
        var dto   = pack.ToDto([]);

        await FireAsync(after, before, new PackUpserted(dto));
        return new SuccessPack(dto);
    }

    public async Task<IPackResult> UpdatePack(Guid packId, IonPartial<ExpressionPack> patch)
    {
        var callerId = this.GetUserId();
        var rights   = await RightsAsync(callerId);

        if (!rights.MayCreate)
            return new FailedPack(ExpressionError.FORBIDDEN);

        await using var ctx = await context.CreateDbContextAsync();

        var pack = await ctx.ExpressionPacks.FirstOrDefaultAsync(p => p.Id == packId && p.SpaceId == SpaceId);
        if (pack is null)
            return new FailedPack(ExpressionError.NOT_FOUND);
        if (!rights.MayChange(pack.CreatorId, callerId))
            return new FailedPack(ExpressionError.FORBIDDEN);

        var title = patch.GetField(x => x.title);
        var slug  = patch.GetField(x => x.slug);
        var cover = patch.GetField(x => x.coverItemId);

        if (title.IsRemoved || (title.HasValue && !ExpressionLimits.IsValidPackTitle(title.Value)))
            return new FailedPack(ExpressionError.INVALID_FORMAT);
        if (slug.IsRemoved || (slug.HasValue && !ExpressionLimits.IsValidPackSlug(slug.Value)))
            return new FailedPack(ExpressionError.INVALID_FORMAT);

        var newTitle = title.HasValue ? title.Value! : pack.Title;
        var newSlug  = slug.HasValue ? slug.Value! : pack.Slug;
        var newCover = cover.HasValue || cover.IsRemoved ? cover.Value : pack.CoverItemId;

        if (newCover is { } coverId && newCover != pack.CoverItemId
         && !await ctx.ExpressionItems.AnyAsync(i => i.Id == coverId && i.PackId == packId))
            return new FailedPack(ExpressionError.NOT_FOUND);

        if (newTitle == pack.Title && newSlug == pack.Slug && newCover == pack.CoverItemId)
            return new SuccessPack(await PackAsync(packId) ?? pack.ToDto([]));

        if (newSlug != pack.Slug && await ctx.ExpressionPacks.AnyAsync(p => p.SpaceId == SpaceId && p.Slug == newSlug))
            return new FailedPack(ExpressionError.NAME_TAKEN);

        if (!await TakeMutationAsync(rights.Bot))
            return new FailedPack(ExpressionError.RATE_LIMITED);

        var before = (await SnapshotAsync()).Version;

        pack.Title       = newTitle;
        pack.Slug        = newSlug;
        pack.CoverItemId = newCover;
        pack.Version++;

        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (e.IsUniqueViolation())
        {
            return new FailedPack(ExpressionError.NAME_TAKEN);
        }

        var after = await RefreshAsync();

        await FireAsync(after, before, new PackUpserted(pack.ToDto([])));
        return new SuccessPack(await PackAsync(packId) ?? pack.ToDto([]));
    }

    public async Task<IPackResult> DeletePack(Guid packId)
    {
        var callerId = this.GetUserId();
        var rights   = await RightsAsync(callerId);

        if (rights.Bot || !rights.MayCreate)
            return new FailedPack(ExpressionError.FORBIDDEN);

        await using var ctx = await context.CreateDbContextAsync();

        var pack = await ctx.ExpressionPacks
           .Include(p => p.Items)
           .FirstOrDefaultAsync(p => p.Id == packId && p.SpaceId == SpaceId);

        if (pack is null)
            return new FailedPack(ExpressionError.NOT_FOUND);
        if (!rights.MayChange(pack.CreatorId, callerId))
            return new FailedPack(ExpressionError.FORBIDDEN);

        if (!await TakeMutationAsync(rights.Bot))
            return new FailedPack(ExpressionError.RATE_LIMITED);

        var before = (await SnapshotAsync()).Version;
        var now    = DateTimeOffset.UtcNow;

        // Soft: the rows and their file references stay, so messages that carry them still render.
        foreach (var item in pack.Items)
        {
            item.IsDeleted = true;
            item.DeletedAt = now;
        }

        pack.IsDeleted = true;
        pack.DeletedAt = now;
        pack.ItemCount = 0;
        pack.Version++;

        await ctx.SaveChangesAsync();
        await ForgetItemsAsync(pack.Items.Select(i => i.Id));

        var after = await RefreshAsync();

        await FireAsync(after, before, new PackDeleted(packId));
        return new SuccessPack(pack.ToDto([]));
    }

    public async Task<IReorderResult> ReorderPacks(ExpressionKind kind, List<Guid> ordered)
    {
        var rights = await RightsAsync(this.GetUserId());

        if (rights.Bot || !rights.Manage)
            return new FailedReorder(ExpressionError.FORBIDDEN);

        await using var ctx = await context.CreateDbContextAsync();

        var packs = await ctx.ExpressionPacks
           .Where(p => p.SpaceId == SpaceId && p.Kind == kind)
           .ToDictionaryAsync(p => p.Id);

        if (!IsPermutation(ordered, packs.Keys))
            return new FailedReorder(ExpressionError.INVALID_FORMAT);

        var moved = ordered.Select((id, index) => (Pack: packs[id], Index: index)).Where(x => x.Pack.SortOrder != x.Index).ToList();
        if (moved.Count == 0)
            return new SuccessReorder(new IonArray<Guid>(ordered));

        if (!await TakeMutationAsync(rights.Bot))
            return new FailedReorder(ExpressionError.RATE_LIMITED);

        var before = (await SnapshotAsync()).Version;

        foreach (var (pack, index) in moved)
        {
            pack.SortOrder = index;
            pack.Version++;
        }

        await ctx.SaveChangesAsync();

        var after = await RefreshAsync();

        await FireAsync(after, before, new PacksReordered(kind, new IonArray<Guid>(ordered.ToList())));
        return new SuccessReorder(new IonArray<Guid>(ordered));
    }

    // ── snapshot ────────────────────────────────────────────────────────────────────────────────

    private async Task<Versioned<IonArray<ExpressionPack>>> SnapshotAsync()
    {
        if (snapshot is { } current)
            return current;

        var seen   = generation;
        var loaded = await cache.GetOrCreateAsync(CacheKey(SpaceId), (context, SpaceId, s3),
            static async (state, ct) => await ReadAsync(state.context, state.SpaceId, state.s3, ct),
            CacheOptions, [ISpaceReadCache.SpaceTag(SpaceId)]);

        if (seen == generation)
            Remember(loaded);

        return snapshot ?? loaded;
    }

    /// <summary>After a write: reads the rows back, replaces the shared entry and returns the new token.</summary>
    private async Task<string> RefreshAsync()
    {
        var value = await ReadAsync(context, SpaceId, s3, CancellationToken.None);

        try
        {
            await cache.SetAsync(CacheKey(SpaceId), value, CacheOptions, [ISpaceReadCache.SpaceTag(SpaceId)]);
        }
        catch (Exception e)
        {
            // Only a later activation reads the shared entry, and it expires on its own.
            logger.LogWarning(e, "could not store the expressions snapshot of space {SpaceId}", SpaceId);
        }

        generation++;
        Remember(value);
        return value.Version;
    }

    /// <summary>Drops the items' status emoji entries, see <see cref="ExpressionItemCache"/>.</summary>
    private async Task ForgetItemsAsync(IEnumerable<Guid> itemIds)
    {
        try
        {
            await ExpressionItemCache.InvalidateAsync(cache, itemIds);
        }
        catch (Exception e)
        {
            // They expire on their own.
            logger.LogWarning(e, "could not drop cached expression items of space {SpaceId}", SpaceId);
        }
    }

    private void Remember(Versioned<IonArray<ExpressionPack>> value)
    {
        snapshot  = value;
        liveItems = value.Value.SelectMany(p => p.items).ToDictionary(i => i.itemId);
    }

    private async Task<ExpressionPack?> PackAsync(Guid packId)
        => (await SnapshotAsync()).Value.FirstOrDefault(p => p.packId == packId);

    private static async Task<Versioned<IonArray<ExpressionPack>>> ReadAsync(IDbContextFactory<ApplicationDbContext> factory,
        Guid spaceId, IS3StorageService s3, CancellationToken ct)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // The soft-delete filter applies to the included items as well.
        var packs = await ctx.ExpressionPacks
           .AsNoTracking()
           .AsSplitQuery()
           .Include(p => p.Items)
           .Where(p => p.SpaceId == spaceId)
           .OrderBy(p => p.Kind)
           .ThenBy(p => p.SortOrder)
           .ThenBy(p => p.CreatedAt)
           .ToListAsync(ct);

        var value = packs
           .Select(p => p.ToDto(p.Items
               .OrderBy(i => i.SortOrder)
               .ThenBy(i => i.CreatedAt)
               .Select(i => i.ToDto(s3.GetFileDownloadUrl))))
           .ToList();

        return new Versioned<IonArray<ExpressionPack>>(ExpressionsVersion.Of(value), new IonArray<ExpressionPack>(value));
    }

    // ── shared ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A bot never deletes or reorders, and changes only what it made, whatever it was granted.</summary>
    private readonly record struct Rights(bool Create, bool Manage, bool Bot)
    {
        public bool MayCreate => Create || Manage;

        /// <summary>One's own with CreateExpressions, anybody's with ManageExpressions.</summary>
        public bool MayChange(Guid ownerId, Guid callerId) => Bot ? MayCreate && ownerId == callerId : Manage || (Create && ownerId == callerId);
    }

    private async Task<Rights> RightsAsync(Guid callerId)
        => new(await entitlementChecker.HasAccessAsync(SpaceId, callerId, ArgonEntitlement.CreateExpressions),
            await entitlementChecker.HasAccessAsync(SpaceId, callerId, ArgonEntitlement.ManageExpressions),
            await IsBotAsync(callerId));

    // A user never turns into a bot or back, so the answer holds for the activation.
    private readonly Dictionary<Guid, bool> bots = new();

    private async Task<bool> IsBotAsync(Guid userId)
    {
        if (bots.TryGetValue(userId, out var bot))
            return bot;

        await using var ctx = await context.CreateDbContextAsync();

        // Older bot accounts carry no BotEntityId; the bot row's back-reference names them too.
        bot = await ctx.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId && u.BotEntityId != null)
           || await ctx.BotEntities.IgnoreQueryFilters().AnyAsync(b => b.BotAsUserId == userId);

        return bots[userId] = bot;
    }

    /// <summary>Takes one of the space's mutations for the current minute; bots draw on a budget of their own.</summary>
    private async Task<bool> TakeMutationAsync(bool bot)
    {
        var key   = bot ? $"expressions:rate:{SpaceId}:bot" : $"expressions:rate:{SpaceId}";
        var limit = bot ? Limits.BotMutationsPerMinute : Limits.MutationsPerMinute;

        try
        {
            var count = await counters.StringIncrementAsync(key);

            // Re-armed once past the limit too, so a window whose expiry never landed cannot lock the space.
            if (count == 1 || count == limit + 1)
                await counters.UpdateStringExpirationAsync(key, TimeSpan.FromMinutes(1));

            return count <= limit;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "expression rate limit unavailable for space {SpaceId}; letting the change through", SpaceId);
            return true;
        }
    }

    private async Task FireAsync(string version, string baseVersion, IExpressionDelta delta)
    {
        try
        {
            await appHubServer.BroadcastSpace(new SpaceExpressionsChanged(SpaceId, version, baseVersion, delta), SpaceId);
        }
        catch (Exception e)
        {
            // Written already; a client that missed this sees the new token on its next read.
            logger.LogWarning(e, "could not announce an expressions change in space {SpaceId}", SpaceId);
        }
    }

    private static bool IsPermutation(List<Guid>? ordered, IReadOnlyCollection<Guid> live)
        => ordered is not null
        && ordered.Count == live.Count
        && ordered.Distinct().Count() == ordered.Count
        && ordered.All(live.Contains);

    private async Task<int> BoostLevelAsync(ApplicationDbContext ctx)
        => await ctx.Spaces.AsNoTracking().Where(s => s.Id == SpaceId).Select(s => s.BoostLevel).FirstOrDefaultAsync();
}
