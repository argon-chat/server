using Argon.Cassandra.Mapping;

namespace Argon.Cassandra.Collections;

using Mapping;

/// <summary>
/// Metadata for collection properties
/// </summary>
public class CollectionMetadata
{
    public string         PropertyName   { get; set; } = string.Empty;
    public string         ColumnName     { get; set; } = string.Empty;
    public CollectionType CollectionType { get; set; }
    public Type           PropertyType   { get; set; } = null!;
    public Type?          ElementType    { get; set; }
    public Type?          KeyType        { get; set; }
    public Type?          ValueType      { get; set; }
    public PropertyInfo   PropertyInfo   { get; set; } = null!;

    public static CollectionMetadata? Create(PropertyInfo property)
    {
        var collectionAttribute = property.GetCustomAttribute<CollectionAttribute>();
        if (collectionAttribute == null)
        {
            // Try to infer collection type from property type
            var propertyType = property.PropertyType;

            if (IsListType(propertyType))
                return CreateForList(property, propertyType);

            if (IsSetType(propertyType))
                return CreateForSet(property, propertyType);

            return IsMapType(propertyType) ? CreateForMap(property, propertyType) : null;
        }

        var metadata = new CollectionMetadata
        {
            PropertyName   = property.Name,
            ColumnName     = GetColumnName(property),
            CollectionType = collectionAttribute.CollectionType,
            PropertyType   = property.PropertyType,
            PropertyInfo   = property
        };

        // Determine element types
        switch (collectionAttribute.CollectionType)
        {
            case CollectionType.List:
            case CollectionType.Set:
                metadata.ElementType = collectionAttribute.ValueType ?? GetGenericArgument(property.PropertyType, 0);
                break;
            case CollectionType.Map:
                metadata.KeyType   = collectionAttribute.KeyType ?? GetGenericArgument(property.PropertyType, 0);
                metadata.ValueType = collectionAttribute.ValueType ?? GetGenericArgument(property.PropertyType, 1);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return metadata;
    }

    private static CollectionMetadata CreateForList(PropertyInfo property, Type propertyType)
        => new()
        {
            PropertyName   = property.Name,
            ColumnName     = GetColumnName(property),
            CollectionType = CollectionType.List,
            PropertyType   = propertyType,
            ElementType    = GetGenericArgument(propertyType, 0),
            PropertyInfo   = property
        };

    private static CollectionMetadata CreateForSet(PropertyInfo property, Type propertyType)
        => new()
        {
            PropertyName   = property.Name,
            ColumnName     = GetColumnName(property),
            CollectionType = CollectionType.Set,
            PropertyType   = propertyType,
            ElementType    = GetGenericArgument(propertyType, 0),
            PropertyInfo   = property
        };

    private static CollectionMetadata CreateForMap(PropertyInfo property, Type propertyType)
        => new()
        {
            PropertyName   = property.Name,
            ColumnName     = GetColumnName(property),
            CollectionType = CollectionType.Map,
            PropertyType   = propertyType,
            KeyType        = GetGenericArgument(propertyType, 0),
            ValueType      = GetGenericArgument(propertyType, 1),
            PropertyInfo   = property
        };

    private static string GetColumnName(PropertyInfo property)
    {
        var columnAttribute = property.GetCustomAttribute<ColumnAttribute>();
        return columnAttribute?.Name ?? property.Name.ToLowerInvariant();
    }

    private static bool IsListType(Type type)
        => type.IsGenericType &&
           (type.GetGenericTypeDefinition() == typeof(List<>) ||
            type.GetGenericTypeDefinition() == typeof(IList<>) ||
            type.GetGenericTypeDefinition() == typeof(ICollection<>));

    private static bool IsSetType(Type type)
        => type.IsGenericType &&
           (type.GetGenericTypeDefinition() == typeof(HashSet<>) ||
            type.GetGenericTypeDefinition() == typeof(ISet<>));

    private static bool IsMapType(Type type)
        => type.IsGenericType &&
           (type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
            type.GetGenericTypeDefinition() == typeof(IDictionary<,>));

    private static Type? GetGenericArgument(Type type, int index)
    {
        if (!type.IsGenericType) return null;
        var args = type.GetGenericArguments();
        return args.Length > index ? args[index] : null;
    }
}

/// <summary>
/// Represents a collection update operation
/// </summary>
public class CollectionUpdate
{
    public string              PropertyName { get; set; } = string.Empty;
    public CollectionOperation Operation    { get; set; }
    public object?             Key          { get; set; }
    public object?             Value        { get; set; }
}

/// <summary>
/// Types of collection operations
/// </summary>
public enum CollectionOperation
{
    ListAppend,
    ListPrepend,
    ListRemove,
    SetAdd,
    SetRemove,
    MapPut,
    MapRemove,
    Replace
}

/// <summary>
/// Extension methods for collection support
/// </summary>
public static class CollectionExtensions
{
    /// <summary>
    /// Creates a collection update builder for an entity
    /// </summary>
    public static CollectionUpdateBuilder UpdateCollections<T>(this T entity) where T : class
        => new();

    /// <summary>
    /// Checks if a type is a supported collection type
    /// </summary>
    public static bool IsSupportedCollectionType(this Type type)
    {
        if (!type.IsGenericType) return false;

        var genericType = type.GetGenericTypeDefinition();
        return genericType == typeof(List<>) ||
               genericType == typeof(IList<>) ||
               genericType == typeof(ICollection<>) ||
               genericType == typeof(HashSet<>) ||
               genericType == typeof(ISet<>) ||
               genericType == typeof(Dictionary<,>) ||
               genericType == typeof(IDictionary<,>);
    }

    /// <summary>
    /// Gets the collection type for a property type
    /// </summary>
    public static CollectionType? GetCollectionType(this Type type)
    {
        if (!type.IsGenericType) return null;

        var genericType = type.GetGenericTypeDefinition();

        if (genericType == typeof(List<>) ||
            genericType == typeof(IList<>) ||
            genericType == typeof(ICollection<>))
        {
            return CollectionType.List;
        }

        if (genericType == typeof(HashSet<>) ||
            genericType == typeof(ISet<>))
        {
            return CollectionType.Set;
        }

        if (genericType == typeof(Dictionary<,>) ||
            genericType == typeof(IDictionary<,>))
        {
            return CollectionType.Map;
        }

        return null;
    }
}