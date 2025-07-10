namespace Argon.Cassandra.Mapping;

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