namespace Argon.Cassandra.Mapping;

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