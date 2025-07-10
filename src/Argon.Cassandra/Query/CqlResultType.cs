namespace Argon.Cassandra.Query;

/// <summary>
/// Specifies the type of result expected from a CQL query.
/// </summary>
public enum CqlResultType
{
    /// <summary>
    /// Return an enumerable of results.
    /// </summary>
    Enumerable,

    /// <summary>
    /// Return a single result, throw if none or multiple.
    /// </summary>
    Single,

    /// <summary>
    /// Return a single result or default, throw if multiple.
    /// </summary>
    SingleOrDefault,

    /// <summary>
    /// Return the first result, throw if none.
    /// </summary>
    First,

    /// <summary>
    /// Return the first result or default.
    /// </summary>
    FirstOrDefault,

    /// <summary>
    /// Return the count of results.
    /// </summary>
    Count,

    /// <summary>
    /// Return whether any results exist.
    /// </summary>
    Any
}