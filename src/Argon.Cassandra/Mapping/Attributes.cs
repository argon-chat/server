namespace Argon.Cassandra.Mapping;

/// <summary>
/// Specifies the sort order for clustering columns
/// </summary>
public enum SortOrder
{
    /// <summary>
    /// Ascending order (default)
    /// </summary>
    Ascending,
    
    /// <summary>
    /// Descending order
    /// </summary>
    Descending
}

/// <summary>
/// Specifies the type of index.
/// </summary>
public enum IndexType
{
    /// <summary>
    /// Default secondary index.
    /// </summary>
    Default,
    
    /// <summary>
    /// SASI (SSTable Attached Secondary Index).
    /// </summary>
    SASI,
    
    /// <summary>
    /// Custom index implementation.
    /// </summary>
    Custom
}

/// <summary>
/// Specifies that the class is a Cassandra table.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class TableAttribute : Attribute
{
    /// <summary>
    /// Gets the name of the table.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets the keyspace name for the table.
    /// </summary>
    public string? Keyspace { get; set; }

    /// <summary>
    /// Initializes a new instance of the TableAttribute class.
    /// </summary>
    /// <param name="name">The name of the table.</param>
    public TableAttribute(string name)
        => Name = name ?? throw new ArgumentNullException(nameof(name));
}

/// <summary>
/// Specifies that a property is a partition key column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class PartitionKeyAttribute : Attribute
{
    /// <summary>
    /// Gets the order of the partition key column.
    /// </summary>
    public int Order { get; }

    /// <summary>
    /// Initializes a new instance of the PartitionKeyAttribute class.
    /// </summary>
    /// <param name="order">The order of the partition key column.</param>
    public PartitionKeyAttribute(int order = 0)
        => Order = order;
}

/// <summary>
/// Specifies that a property is a clustering key column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class ClusteringKeyAttribute : Attribute
{
    /// <summary>
    /// Gets the order of the clustering key column.
    /// </summary>
    public int Order { get; }

    /// <summary>
    /// Gets whether the clustering key is descending.
    /// </summary>
    public bool Descending { get; }

    /// <summary>
    /// Initializes a new instance of the ClusteringKeyAttribute class.
    /// </summary>
    /// <param name="order">The order of the clustering key column.</param>
    /// <param name="descending">Whether the clustering key is descending.</param>
    public ClusteringKeyAttribute(int order = 0, bool descending = false)
    {
        Order = order;
        Descending = descending;
    }
}

/// <summary>
/// Specifies the name of a column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class ColumnAttribute : Attribute
{
    /// <summary>
    /// Gets the name of the column.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets the type of the column in Cassandra.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Initializes a new instance of the ColumnAttribute class.
    /// </summary>
    /// <param name="name">The name of the column.</param>
    public ColumnAttribute(string name)
        => Name = name ?? throw new ArgumentNullException(nameof(name));
}

/// <summary>
/// Specifies that a property should not be mapped to a column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class NotMappedAttribute : Attribute
{
}

/// <summary>
/// Specifies that a property is a counter column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class CounterAttribute : Attribute
{
}

/// <summary>
/// Specifies that a property is a static column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class StaticColumnAttribute : Attribute
{
}

/// <summary>
/// Specifies that a property should be indexed.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class IndexAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the name of the index.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the type of index.
    /// </summary>
    public IndexType Type { get; set; } = IndexType.Default;

    /// <summary>
    /// Initializes a new instance of the IndexAttribute class.
    /// </summary>
    public IndexAttribute()
    {
    }

    /// <summary>
    /// Initializes a new instance of the IndexAttribute class with a name.
    /// </summary>
    /// <param name="name">The name of the index.</param>
    public IndexAttribute(string name)
        => Name = name;
}

/// <summary>
/// Specifies options for a user-defined type (UDT).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class UserDefinedTypeAttribute : Attribute
{
    /// <summary>
    /// Gets the name of the UDT.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets the keyspace name for the UDT.
    /// </summary>
    public string? Keyspace { get; set; }

    /// <summary>
    /// Initializes a new instance of the UserDefinedTypeAttribute class.
    /// </summary>
    /// <param name="name">The name of the UDT.</param>
    public UserDefinedTypeAttribute(string name)
        => Name = name ?? throw new ArgumentNullException(nameof(name));
}
