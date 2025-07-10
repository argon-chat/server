namespace Argon.Cassandra.Mapping;

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