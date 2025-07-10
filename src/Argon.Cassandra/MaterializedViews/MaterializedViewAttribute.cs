namespace Argon.Cassandra.MaterializedViews;

/// <summary>
/// Marks a class as a Cassandra Materialized View
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class MaterializedViewAttribute(string viewName, string baseTable) : Attribute
{
    public string  ViewName  { get; } = viewName ?? throw new ArgumentNullException(nameof(viewName));
    public string  BaseTable { get; } = baseTable ?? throw new ArgumentNullException(nameof(baseTable));
    public string? Keyspace  { get; set; }
}