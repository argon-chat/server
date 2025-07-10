namespace Argon.Cassandra.Mapping;

/// <summary>
/// Metadata cache for entity types.
/// </summary>
public static class EntityMetadataCache
{
    private static readonly Dictionary<Type, EntityMetadata> cache = new();
    private static readonly Lock                             @lock = new();

    /// <summary>
    /// Gets the metadata for an entity type.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <returns>The entity metadata.</returns>
    public static EntityMetadata GetMetadata(Type entityType)
    {
        lock (@lock)
        {
            if (cache.TryGetValue(entityType, out var metadata))
                return metadata;

            metadata          = new EntityMetadata(entityType);
            cache[entityType] = metadata;
            return metadata;
        }
    }

    /// <summary>
    /// Gets the metadata for an entity type.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <returns>The entity metadata.</returns>
    public static EntityMetadata GetMetadata<T>() where T : class
        => GetMetadata(typeof(T));

    internal static void OnGenerateEntityMetadata(Type type, EntityMetadata metadata)
    {
        lock (@lock)
        {
            cache.Add(type, metadata);
        }
    }
}