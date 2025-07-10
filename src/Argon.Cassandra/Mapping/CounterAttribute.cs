namespace Argon.Cassandra.Mapping;

/// <summary>
/// Specifies that a property is a counter column.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public class CounterAttribute : Attribute
{
}