namespace Argon.Grains.Interfaces;

using ion.runtime;
using Orleans.Concurrency;

/// <summary>
/// A space's sticker and custom emoji packs: the only writer of their rows, and the cached snapshot
/// the message path resolves entities against. Keyed by space id; the caller is read from the request
/// context, as <see cref="ISpaceGrain"/> does.
/// </summary>
[Alias($"Argon.Grains.Interfaces.{nameof(ISpaceExpressionsGrain)}")]
public interface ISpaceExpressionsGrain : IGrainWithGuidKey
{
    /// <summary>Every live pack, or <c>packs = null</c> when <paramref name="known"/> is still current. Members only.</summary>
    [Alias(nameof(GetExpressions)), AlwaysInterleave]
    Task<ExpressionsSnapshot> GetExpressions(string? known);

    [Alias(nameof(CreatePack))]
    Task<IPackResult> CreatePack(ExpressionKind kind, string title, string slug);

    [Alias(nameof(UpdatePack))]
    Task<IPackResult> UpdatePack(Guid packId, IonPartial<ExpressionPack> patch);

    [Alias(nameof(DeletePack))]
    Task<IPackResult> DeletePack(Guid packId);

    [Alias(nameof(ReorderPacks))]
    Task<IReorderResult> ReorderPacks(ExpressionKind kind, List<Guid> ordered);

    [Alias(nameof(BeginUploadExpression))]
    Task<IUploadFileResult> BeginUploadExpression(ExpressionKind kind, ExpressionFormat format, string contentType, long size);

    /// <summary>Finalizes, validates and moderates the upload, then adds it to the pack.</summary>
    [Alias(nameof(AddItem)), ResponseTimeout("00:02:00")]
    Task<IItemResult> AddItem(Guid packId, Guid blobId, Guid? thumbBlobId, string name, List<string> emoji, List<string> keywords,
        byte[]? outline);

    [Alias(nameof(UpdateItem))]
    Task<IItemResult> UpdateItem(Guid itemId, IonPartial<ExpressionItem> patch);

    [Alias(nameof(DeleteItem))]
    Task<IItemResult> DeleteItem(Guid itemId);

    [Alias(nameof(ReorderItems))]
    Task<IReorderResult> ReorderItems(Guid packId, List<Guid> ordered);

    /// <summary>The live items among <paramref name="itemIds"/>, from the cached snapshot. No caller check.</summary>
    /// <remarks>Pass a <see cref="List{T}"/>: a collection expression's compiler-made type does not cross a grain call.</remarks>
    [Alias(nameof(ResolveLiveItemsAsync)), AlwaysInterleave]
    Task<IReadOnlyDictionary<Guid, ExpressionItem>> ResolveLiveItemsAsync(IReadOnlyCollection<Guid> itemIds);

    /// <summary>Soft-deletes every pack and item and releases their files. Called by <see cref="ISpaceGrain.DeleteSpace"/>.</summary>
    [Alias(nameof(OnSpaceDeletedAsync))]
    Task OnSpaceDeletedAsync();
}
