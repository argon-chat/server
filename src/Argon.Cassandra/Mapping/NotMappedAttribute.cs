namespace Argon.Cassandra.Mapping;

/// <summary>
/// Specifies that a property should not be mapped to a column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class NotMappedAttribute : Attribute
{
}