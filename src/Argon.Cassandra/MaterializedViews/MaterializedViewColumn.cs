namespace Argon.Cassandra.MaterializedViews;

using Mapping;

/// <summary>
/// Column metadata for materialized views
/// </summary>
public class MaterializedViewColumn
{
    public string       PropertyName    { get; set; } = string.Empty;
    public string       ColumnName      { get; set; } = string.Empty;
    public Type         PropertyType    { get; set; } = null!;
    public PropertyInfo PropertyInfo    { get; set; } = null!;
    public bool         IsPartitionKey  { get; set; }
    public bool         IsClusteringKey { get; set; }
    public int          Order           { get; set; }
    public SortOrder    SortOrder       { get; set; } = SortOrder.Ascending;
}