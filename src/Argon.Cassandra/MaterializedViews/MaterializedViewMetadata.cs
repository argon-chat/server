namespace Argon.Cassandra.MaterializedViews;

using Mapping;

/// <summary>
/// Metadata for Materialized Views
/// </summary>
public class MaterializedViewMetadata
{
    public string                       ViewName       { get; set; } = string.Empty;
    public string                       BaseTable      { get; set; } = string.Empty;
    public string?                      Keyspace       { get; set; }
    public string?                      WhereClause    { get; set; }
    public Type                         ViewType       { get; set; } = null!;
    public Type                         BaseEntityType { get; set; } = null!;
    public List<MaterializedViewColumn> Columns        { get; set; } = new();
    public List<MaterializedViewColumn> PartitionKeys  { get; set; } = new();
    public List<MaterializedViewColumn> ClusteringKeys { get; set; } = new();

    public static MaterializedViewMetadata Create(Type viewType, Type baseEntityType)
    {
        var mvAttribute = viewType.GetCustomAttribute<MaterializedViewAttribute>();
        if (mvAttribute == null)
        {
            throw new InvalidOperationException($"Type {viewType.Name} must be marked with [MaterializedView] attribute.");
        }

        var whereAttribute = viewType.GetCustomAttribute<MaterializedViewWhereAttribute>();

        var metadata = new MaterializedViewMetadata
        {
            ViewName       = mvAttribute.ViewName,
            BaseTable      = mvAttribute.BaseTable,
            Keyspace       = mvAttribute.Keyspace,
            WhereClause    = whereAttribute?.WhereClause,
            ViewType       = viewType,
            BaseEntityType = baseEntityType
        };        // Get all properties that can be mapped
        var properties = viewType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
           .Where(p => p.CanRead && !p.GetCustomAttributes<NotMappedAttribute>().Any());

        foreach (var property in properties)
        {
            var column = new MaterializedViewColumn
            {
                PropertyName = property.Name,
                ColumnName   = GetColumnName(property),
                PropertyType = property.PropertyType,
                PropertyInfo = property
            };

            metadata.Columns.Add(column);

            // Check for partition key
            if (property.GetCustomAttribute<PartitionKeyAttribute>() != null)
            {
                var partitionKey = property.GetCustomAttribute<PartitionKeyAttribute>()!;
                column.IsPartitionKey = true;
                column.Order          = partitionKey.Order;
                metadata.PartitionKeys.Add(column);
            }

            // Check for clustering key            if (property.GetCustomAttribute<ClusteringKeyAttribute>() != null)
            {
                var clusteringKey = property.GetCustomAttribute<ClusteringKeyAttribute>()!;
                column.IsClusteringKey = true;
                column.Order           = clusteringKey.Order;
                column.SortOrder       = clusteringKey.Descending ? SortOrder.Descending : SortOrder.Ascending;
                metadata.ClusteringKeys.Add(column);
            }
        }

        // Sort keys by order
        metadata.PartitionKeys  = metadata.PartitionKeys.OrderBy(k => k.Order).ToList();
        metadata.ClusteringKeys = metadata.ClusteringKeys.OrderBy(k => k.Order).ToList();

        return metadata;
    }

    private static string GetColumnName(PropertyInfo property)
    {        var columnAttribute = property.GetCustomAttribute<ColumnAttribute>();
        return columnAttribute?.Name ?? property.Name.ToLowerInvariant();
    }
}