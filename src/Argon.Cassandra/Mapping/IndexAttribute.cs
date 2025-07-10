namespace Argon.Cassandra.Mapping;

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