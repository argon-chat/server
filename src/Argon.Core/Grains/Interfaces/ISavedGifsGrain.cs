namespace Argon.Core.Grains.Interfaces;

public interface ISavedGifsGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetSavedGifsAsync))]
    Task<List<SavedGif>> GetSavedGifsAsync(int page, int perPage, CancellationToken ct = default);

    [Alias(nameof(SaveGifAsync))]
    Task<ISaveGifResult> SaveGifAsync(string slug, CancellationToken ct = default);

    [Alias(nameof(RemoveSavedGifAsync))]
    Task<bool> RemoveSavedGifAsync(Guid savedGifId, CancellationToken ct = default);
}
