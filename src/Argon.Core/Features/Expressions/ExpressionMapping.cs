namespace Argon.Features.Expressions;

using ion.runtime;

public static class ExpressionMapping
{
    /// <param name="fileUrl">Builds a file's public address; null leaves the URLs empty.</param>
    public static ExpressionItem ToDto(this ExpressionItemEntity e, Func<Guid, string>? fileUrl = null)
        => new(e.Id, e.PackId, e.SpaceId, e.Kind, e.Format, e.Name, e.FileId, e.ThumbFileId, e.Width, e.Height, e.FileSize,
            new IonArray<string>(e.Emoji.ToList()), new IonArray<string>(e.Keywords.ToList()), Bytes(e.Outline), e.TextColor,
            e.SortOrder, fileUrl?.Invoke(e.FileId), e.ThumbFileId is { } thumb ? fileUrl?.Invoke(thumb) : null, e.CreatorId);

    public static ExpressionPack ToDto(this ExpressionPackEntity p, IEnumerable<ExpressionItem> items)
        => new(p.Id, p.SpaceId, p.Kind, p.Title, p.Slug, p.CoverItemId, p.SortOrder, p.Version,
            new IonArray<ExpressionItem>(items.ToList()), p.CreatorId);

    // Typed on purpose: `x is null ? null : new IonBytes(x)` converts the null through byte[] into an empty value.
    public static IonBytes? Bytes(byte[]? value)
        => value is null ? (IonBytes?)null : new IonBytes(value);
}
