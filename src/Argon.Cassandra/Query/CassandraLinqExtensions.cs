namespace Argon.Cassandra.Query;

/// <summary>
/// Extension methods for LINQ expressions to provide Cassandra-specific functionality
/// </summary>
public static class CassandraLinqExtensions
{
    /// <summary>
    /// Enables token-based pagination for large result sets
    /// </summary>
    public static IQueryable<T> Token<T>(this IQueryable<T> source, object tokenValue) where T : class
    // This would be implemented in the query provider to add TOKEN() function support
        => throw new NotImplementedException("Token-based pagination will be implemented in the query provider.");

    /// <summary>
    /// Allows filtering on non-indexed columns (use with caution)
    /// </summary>
    public static IQueryable<T> AllowFiltering<T>(this IQueryable<T> source) where T : class
    // This would be implemented in the query provider to add ALLOW FILTERING
        => throw new NotImplementedException("Allow filtering will be implemented in the query provider.");

    /// <summary>
    /// Specifies consistency level for the query
    /// </summary>
    public static IQueryable<T> WithConsistency<T>(this IQueryable<T> source, ConsistencyLevel consistency) where T : class
    // This would be implemented in the query provider
        => throw new NotImplementedException("Consistency level specification will be implemented in the query provider.");
}