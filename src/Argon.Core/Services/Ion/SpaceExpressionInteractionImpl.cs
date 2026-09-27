namespace Argon.Services.Ion;

using ion.runtime;

public sealed class SpaceExpressionInteractionImpl : ISpaceExpressionInteraction
{
    public async Task<ExpressionsSnapshot> GetExpressions(Guid spaceId, string? known, CancellationToken ct = default)
        => await Grain(spaceId).GetExpressions(known);

    public async Task<IPackResult> CreatePack(Guid spaceId, ExpressionKind kind, string title, string slug, CancellationToken ct = default)
        => await Grain(spaceId).CreatePack(kind, title, slug);

    public async Task<IPackResult> UpdatePack(Guid spaceId, Guid packId, IonPartial<ExpressionPack> patch, CancellationToken ct = default)
        => await Grain(spaceId).UpdatePack(packId, patch);

    public async Task<IPackResult> DeletePack(Guid spaceId, Guid packId, CancellationToken ct = default)
        => await Grain(spaceId).DeletePack(packId);

    public async Task<IReorderResult> ReorderPacks(Guid spaceId, ExpressionKind kind, IonArray<Guid> ordered, CancellationToken ct = default)
        => await Grain(spaceId).ReorderPacks(kind, List(ordered));

    public async Task<IUploadFileResult> BeginUploadExpression(Guid spaceId, ExpressionKind kind, ExpressionFormat format, string contentType,
        long size, CancellationToken ct = default)
        => await Grain(spaceId).BeginUploadExpression(kind, format, contentType, size);

    public async Task<IItemResult> AddItem(Guid spaceId, Guid packId, Guid blobId, Guid? thumbBlobId, string name, IonArray<string> emoji,
        IonArray<string> keywords, IonBytes? outline, CancellationToken ct = default)
        => await Grain(spaceId).AddItem(packId, blobId, thumbBlobId, name, List(emoji), List(keywords), outline?.ToArray());

    public async Task<IItemResult> UpdateItem(Guid spaceId, Guid itemId, IonPartial<ExpressionItem> patch, CancellationToken ct = default)
        => await Grain(spaceId).UpdateItem(itemId, patch);

    public async Task<IItemResult> DeleteItem(Guid spaceId, Guid itemId, CancellationToken ct = default)
        => await Grain(spaceId).DeleteItem(itemId);

    public async Task<IReorderResult> ReorderItems(Guid spaceId, Guid packId, IonArray<Guid> ordered, CancellationToken ct = default)
        => await Grain(spaceId).ReorderItems(packId, List(ordered));

    private ISpaceExpressionsGrain Grain(Guid spaceId) => this.GetGrain<ISpaceExpressionsGrain>(spaceId);

    private static List<T> List<T>(IonArray<T> values) => values.Values?.ToList() ?? [];
}
