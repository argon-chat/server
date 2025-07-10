namespace Argon.Cassandra.MaterializedViews;

/// <summary>
/// Specifies the WHERE clause for a materialized view
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class MaterializedViewWhereAttribute(string whereClause) : Attribute
{
    public string WhereClause { get; } = whereClause ?? throw new ArgumentNullException(nameof(whereClause));
}