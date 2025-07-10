namespace Argon.Cassandra.Mapping;

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