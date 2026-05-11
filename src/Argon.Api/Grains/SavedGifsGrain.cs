namespace Argon.Grains;

using Argon.Core.Grains.Interfaces;
using Argon.Features.Integrations.Klipy;
using Argon.Features.Storage;
using Orleans.Concurrency;

[StatelessWorker]
public class SavedGifsGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IKlipyService klipy,
    IOptions<KlipyOptions> klipyOptions,
    IS3StorageService s3,
    IReferenceCountService refCount,
    ILogger<SavedGifsGrain> logger) : Grain, ISavedGifsGrain
{
    private KlipyOptions Opts => klipyOptions.Value;

    public async Task<List<SavedGif>> GetSavedGifsAsync(int page, int perPage, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await using var db = await context.CreateDbContextAsync(ct);

        var entities = await db.SavedGifs
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.AddedAt)
            .Skip(page * perPage)
            .Take(perPage)
            .ToListAsync(ct);

        return entities.Select(sg =>
        {
            var url = s3.GetDownloadUrl(ResolveCdnKey(sg));
            return new SavedGif(sg.Id, sg.Slug, sg.FileId, url, url, sg.Width, sg.Height, sg.AddedAt.DateTime);
        }).ToList();
    }

    public async Task<ISaveGifResult> SaveGifAsync(string slug, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await using var db = await context.CreateDbContextAsync(ct);

        // Already saved → bump to first place
        var existing = await db.SavedGifs
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Slug == slug, ct);
        if (existing is not null)
        {
            existing.AddedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            var url = s3.GetDownloadUrl(ResolveCdnKey(existing));
            return new SuccessSaveGif(new SavedGif(existing.Id, existing.Slug, existing.FileId, url, url, existing.Width, existing.Height, existing.AddedAt.DateTime));
        }

        await EnforceLimitsAsync(db, userId, ct);

        var cached = await klipy.EnsureCachedAsync(slug, ct);
        if (cached is null)
            return new FailedSaveGif(SaveGifError.NOT_FOUND);

        var (fileId, width, height) = cached.Value;

        var savedGif = new SavedGifEntity
        {
            Id        = Guid.NewGuid(),
            UserId    = userId,
            Slug      = slug,
            FileId    = fileId,
            Width     = width,
            Height    = height,
            AddedAt   = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SavedGifs.Add(savedGif);
        await db.SaveChangesAsync(ct);

        var resultUrl = s3.GetDownloadUrl(klipy.ComputeCachePath(slug));
        return new SuccessSaveGif(new SavedGif(savedGif.Id, slug, fileId, resultUrl, resultUrl, width, height, savedGif.AddedAt.DateTime));
    }

    public async Task<bool> RemoveSavedGifAsync(Guid savedGifId, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await using var db = await context.CreateDbContextAsync(ct);

        var saved = await db.SavedGifs
            .FirstOrDefaultAsync(x => x.Id == savedGifId && x.UserId == userId, ct);
        if (saved is null)
            return false;

        db.SavedGifs.Remove(saved);
        await db.SaveChangesAsync(ct);

        await refCount.DecrementAsync(saved.FileId, ct: ct);
        return true;
    }

    #region Private Helpers

    private async Task EnforceLimitsAsync(ApplicationDbContext db, Guid userId, CancellationToken ct)
    {
        var isPremium = await db.Set<UserEntity>()
            .Where(u => u.Id == userId)
            .Select(u => u.HasActiveUltima)
            .FirstOrDefaultAsync(ct);

        var visibleLimit = isPremium ? Opts.SavedGifLimitPremium : Opts.SavedGifLimitFree;
        var hardLimit    = isPremium ? Opts.SavedGifSlotsPremium : Opts.SavedGifSlotsFree;

        var count = await db.SavedGifs.CountAsync(x => x.UserId == userId, ct);
        if (count < visibleLimit)
            return;

        var toEvict = Math.Max(count - hardLimit + 1, 1);

        var oldest = await db.SavedGifs
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.AddedAt)
            .Take(toEvict)
            .ToListAsync(ct);

        foreach (var gif in oldest)
        {
            db.SavedGifs.Remove(gif);
            await refCount.DecrementAsync(gif.FileId, ct: ct);
            logger.LogInformation("SavedGif evicted: userId={UserId}, slug={Slug}, fileId={FileId}",
                userId, gif.Slug ?? gif.FileId.ToString(), gif.FileId);
        }
    }

    private string ResolveCdnKey(SavedGifEntity sg)
        => sg.Slug is not null
            ? klipy.ComputeCachePath(sg.Slug)
            : $"u/{sg.UserId}/gif/{sg.FileId}";

    #endregion
}
