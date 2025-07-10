namespace Argon.Cassandra.Mapping;

/// <summary>
/// Specifies that a property is a static column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class StaticColumnAttribute : Attribute
{
}