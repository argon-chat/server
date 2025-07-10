namespace Argon.Cassandra.Mapping;

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
        Order      = order;
        Descending = descending;
    }
}