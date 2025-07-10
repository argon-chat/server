namespace Argon.Cassandra.Mapping;

/// <summary>
/// Metadata for a Cassandra table entity.
/// </summary>
public class EntityMetadata
{
    /// <summary>
    /// Gets the entity type.
    /// </summary>
    public Type EntityType { get; }

    /// <summary>
    /// Gets the table name.
    /// </summary>
    public string TableName { get; }

    /// <summary>
    /// Gets the keyspace name.
    /// </summary>
    public string? Keyspace { get; }

    /// <summary>
    /// Gets the partition key properties.
    /// </summary>
    public IReadOnlyList<PropertyInfo> PartitionKeys { get; }

    /// <summary>
    /// Gets the clustering key properties.
    /// </summary>
    public IReadOnlyList<PropertyInfo> ClusteringKeys { get; }

    /// <summary>
    /// Gets all mapped properties.
    /// </summary>
    public IReadOnlyList<PropertyInfo> Properties { get; }

    /// <summary>
    /// Gets the column mapping for properties.
    /// </summary>
    public IReadOnlyDictionary<PropertyInfo, string> ColumnMappings { get; }

    /// <summary>
    /// Gets the static column properties.
    /// </summary>
    public IReadOnlyList<PropertyInfo> StaticColumns { get; }

    /// <summary>
    /// Gets the counter column properties.
    /// </summary>
    public IReadOnlyList<PropertyInfo> CounterColumns { get; }

    /// <summary>
    /// Gets the indexed properties.
    /// </summary>
    public IReadOnlyList<PropertyInfo> IndexedProperties { get; }

    /// <summary>
    /// Get converter
    /// </summary>
    public IReadOnlyDictionary<PropertyInfo, ICassandraConverter> Converters { get; }

    /// <summary>
    /// Initializes a new instance of the EntityMetadata class.
    /// From EntityMetadataBuilder
    /// </summary>
    internal EntityMetadata(
        Type entityType,
        string tableName,
        string? keyspace,
        List<PropertyInfo> properties,
        List<PropertyInfo> partitionKeys,
        List<PropertyInfo> clusteringKeys,
        Dictionary<PropertyInfo, string> columnMappings,
        List<PropertyInfo> staticColumns,
        List<PropertyInfo> counterColumns,
        List<PropertyInfo> indexedProperties,
        Dictionary<PropertyInfo, ICassandraConverter> converters)
    {
        EntityType        = entityType;
        TableName         = tableName;
        Keyspace          = keyspace;
        Properties        = properties.AsReadOnly();
        PartitionKeys     = partitionKeys.AsReadOnly();
        ClusteringKeys    = clusteringKeys.AsReadOnly();
        ColumnMappings    = columnMappings.AsReadOnly();
        StaticColumns     = staticColumns.AsReadOnly();
        CounterColumns    = counterColumns.AsReadOnly();
        IndexedProperties = indexedProperties.AsReadOnly();
        Converters        = converters.AsReadOnly();
    }

    /// <summary>
    /// Initializes a new instance of the EntityMetadata class.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    public EntityMetadata(Type entityType)
    {
        EntityType = entityType ?? throw new ArgumentNullException(nameof(entityType));

        // Get table information
        var tableAttribute = entityType.GetCustomAttribute<TableAttribute>();
        if (tableAttribute == null)
        {
            throw new InvalidOperationException($"Entity type {entityType.Name} must have a [Table] attribute.");
        }

        TableName = tableAttribute.Name;
        Keyspace = tableAttribute.Keyspace;

        // Get all properties that should be mapped
        var allProperties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true } && !p.GetCustomAttributes<NotMappedAttribute>().Any())
            .ToList();

        Properties = allProperties.AsReadOnly();

        // Build column mappings
        var columnMappings = new Dictionary<PropertyInfo, string>();
        foreach (var property in allProperties)
        {
            var columnAttribute = property.GetCustomAttribute<ColumnAttribute>();
            var columnName = columnAttribute?.Name ?? property.Name.ToLowerInvariant();
            columnMappings[property] = columnName;
        }
        ColumnMappings = columnMappings.AsReadOnly();

        // Get partition keys
        var partitionKeys = allProperties
            .Where(p => p.GetCustomAttribute<PartitionKeyAttribute>() != null)
            .OrderBy(p => p.GetCustomAttribute<PartitionKeyAttribute>()!.Order)
            .ToList();

        if (!partitionKeys.Any())
        {
            throw new InvalidOperationException($"Entity type {entityType.Name} must have at least one partition key.");
        }

        PartitionKeys = partitionKeys.AsReadOnly();

        // Get clustering keys
        var clusteringKeys = allProperties
            .Where(p => p.GetCustomAttribute<ClusteringKeyAttribute>() != null)
            .OrderBy(p => p.GetCustomAttribute<ClusteringKeyAttribute>()!.Order)
            .ToList();

        ClusteringKeys = clusteringKeys.AsReadOnly();

        // Get static columns
        StaticColumns = allProperties
            .Where(p => p.GetCustomAttribute<StaticColumnAttribute>() != null)
            .ToList()
            .AsReadOnly();

        // Get counter columns
        CounterColumns = allProperties
            .Where(p => p.GetCustomAttribute<CounterAttribute>() != null)
            .ToList()
            .AsReadOnly();

        // Get indexed properties
        IndexedProperties = allProperties
            .Where(p => p.GetCustomAttribute<IndexAttribute>() != null)
            .ToList()
            .AsReadOnly();
    }    /// <summary>
    /// Gets the primary key values for an entity instance.
    /// </summary>
    /// <param name="entity">The entity instance.</param>
    /// <returns>The primary key values.</returns>
    public object[] GetPrimaryKeyValues(object entity)
    {
        var partitionKeyValues = PartitionKeys.Select(p => p.GetValue(entity) ?? throw new InvalidOperationException($"Partition key {p.Name} cannot be null")).ToArray();
        var clusteringKeyValues = ClusteringKeys.Select(p => p.GetValue(entity) ?? throw new InvalidOperationException($"Clustering key {p.Name} cannot be null")).ToArray();
        
        return partitionKeyValues.Concat(clusteringKeyValues).ToArray();
    }

    /// <summary>
    /// Gets the column name for a property.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <returns>The column name.</returns>
    public string GetColumnName(PropertyInfo property)
        => ColumnMappings.TryGetValue(property, out var columnName) ? columnName : property.Name.ToLowerInvariant();

    /// <summary>
    /// Gets the fully qualified table name.
    /// </summary>
    /// <returns>The fully qualified table name.</returns>
    public string GetFullTableName()
        => string.IsNullOrEmpty(Keyspace) ? TableName : $"{Keyspace}.{TableName}";
}