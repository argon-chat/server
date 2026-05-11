namespace Argon.Services.Ion;

using Argon.Core.Grains.Interfaces;
using Argon.Features.Integrations.Klipy;
using ion.runtime;

public class GifInteractionImpl(
    IKlipyService klipy,
    ILogger<GifInteractionImpl> logger) : IGifInteraction
{
    public async Task<GifSearchResult> GetTrending(int page, int perPage, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        var locale = RequestContext.Get("$caller_country") as string;
        var (items, hasNext) = await klipy.GetTrendingAsync(page, perPage, userId, locale, ct);
        return new GifSearchResult(items.Select(x => MapToGifItem(x, userId)).ToList(), hasNext);
    }

    public async Task<GifSearchResult> Search(string query, int page, int perPage, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        var locale = RequestContext.Get("$caller_country") as string;
        var (items, hasNext) = await klipy.SearchAsync(query, page, perPage, userId, locale, ct);
        return new GifSearchResult(items.Select(x => MapToGifItem(x, userId)).ToList(), hasNext);
    }

    public async Task<IonArray<GifCategory>> GetCategories(CancellationToken ct = default)
    {
        var categories = await klipy.GetCategoriesAsync(ct);
        return new(categories.Select(c => new GifCategory(c.Title, c.Url)).ToList());
    }

    public async Task<IonArray<SavedGif>> GetSavedGifs(int page, int perPage, CancellationToken ct = default)
        => new(await this.GetGrain<ISavedGifsGrain>(this.GetUserId()).GetSavedGifsAsync(page, perPage, ct));

    public async Task<ISaveGifResult> SaveGif(string gifId, string hmac, CancellationToken ct = default)
    {
        var userId = this.GetUserId();

        if (!klipy.ValidateUserHmac(gifId, userId, hmac))
            return new FailedSaveGif(SaveGifError.INVALID_HMAC);

        return await this.GetGrain<ISavedGifsGrain>(userId).SaveGifAsync(gifId, ct);
    }

    public async Task<bool> RemoveSavedGif(Guid savedGifId, CancellationToken ct = default)
        => await this.GetGrain<ISavedGifsGrain>(this.GetUserId()).RemoveSavedGifAsync(savedGifId, ct);

    private GifItem MapToGifItem(KlipyMediaItem item, Guid userId)
    {
        var preview = item.File?.Sm?.Webp ?? item.File?.Xs?.Webp ?? item.File?.Sm?.Gif;
        var full    = item.File?.Md?.Mp4  ?? item.File?.Hd?.Mp4  ?? item.File?.Md?.Gif ?? item.File?.Sm?.Gif;

        return new GifItem(
            gifId:      item.Slug,
            title:      item.Title,
            previewUrl: preview?.Url ?? string.Empty,
            webmUrl:    full?.Url    ?? string.Empty,
            width:      full?.Width  ?? 0,
            height:     full?.Height ?? 0,
            hmac:       klipy.ComputeUserHmac(item.Slug, userId));
    }
}
