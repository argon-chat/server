namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

/// <summary>
/// Custom emoji by item id, for statuses: read through <see cref="Argon.Services.L1L2.ExpressionItemCache"/>.
/// Stateless, keyed by <see cref="Guid.Empty"/>.
/// </summary>
[Alias($"Argon.Grains.Interfaces.{nameof(IExpressionItemDirectoryGrain)}")]
public interface IExpressionItemDirectoryGrain : IGrainWithGuidKey
{
    /// <summary>The live emoji among <paramref name="itemIds"/>; a deleted or unknown id is left out.</summary>
    /// <remarks>Pass a <see cref="List{T}"/>: a collection expression's compiler-made type does not cross a grain call.</remarks>
    [Alias(nameof(ResolveEmojiAsync))]
    Task<IReadOnlyDictionary<Guid, StatusEmoji>> ResolveEmojiAsync(IReadOnlyCollection<Guid> itemIds);

    /// <summary>Nulls each user's status icon while it is still the given reference to a gone item.</summary>
    [Alias(nameof(ForgetStatusIconsAsync)), OneWay]
    Task ForgetStatusIconsAsync(Dictionary<Guid, string> staleIconByUser);
}
