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

    /// <summary>
    /// The Bot API's AddItem: the file arrives as bytes or as the caller's upload and takes the same path as
    /// <see cref="AddItem"/>. There is no client thumbnail, so an animated item needs the server's first frame.
    /// </summary>
    [Alias(nameof(ImportItem)), ResponseTimeout("00:02:00")]
    Task<ImportedItem> ImportItem(ExpressionImport request);

    /// <summary>What the space holds against its limits. Members only.</summary>
    [Alias(nameof(GetQuota)), AlwaysInterleave]
    Task<ExpressionQuota> GetQuota();

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

/// <summary>A file for <see cref="ISpaceExpressionsGrain.ImportItem"/>: its bytes, or the id of the caller's upload.</summary>
[GenerateSerializer, Immutable]
public sealed record ExpressionFileInput(
    [property: Id(0)] Guid?   FileId,
    [property: Id(1)] byte[]? Data);

/// <remarks>Pass <see cref="List{T}"/>s: a collection expression's compiler-made type does not cross a grain call.</remarks>
[GenerateSerializer, Immutable]
public sealed record ExpressionImport(
    [property: Id(0)] Guid                 PackId,
    [property: Id(1)] string               Name,
    [property: Id(2)] List<string>         Emoji,
    [property: Id(3)] List<string>         Keywords,
    [property: Id(4)] bool                 TextColor,
    [property: Id(5)] ExpressionFileInput  File);

/// <summary>
/// <see cref="Item"/> is set when <see cref="Error"/> is <c>NONE</c>. <see cref="RenderUnavailable"/> narrows an
/// <c>INVALID_FORMAT</c>: the file is animated and this server cannot draw its first frame.
/// </summary>
[GenerateSerializer, Immutable]
public sealed record ImportedItem(
    [property: Id(0)] ExpressionError Error,
    [property: Id(1)] ExpressionItem? Item,
    [property: Id(2)] bool            RenderUnavailable = false)
{
    public static readonly ImportedItem Unrenderable = new(ExpressionError.INVALID_FORMAT, null, true);
}

[GenerateSerializer, Immutable]
public sealed record ExpressionQuota(
    [property: Id(0)] int Packs,
    [property: Id(1)] int MaxPacks,
    [property: Id(2)] int Stickers,
    [property: Id(3)] int MaxStickers,
    [property: Id(4)] int Emoji,
    [property: Id(5)] int MaxEmoji,
    [property: Id(6)] int StickersPerPack,
    [property: Id(7)] int EmojiPerPack,
    [property: Id(8)] int BoostLevel);
