namespace Argon.Cassandra.Collections;

/// <summary>
/// Collection operations for Cassandra
/// </summary>
public static class CollectionOperations
{
    /// <summary>
    /// Generates CQL for appending to a list
    /// </summary>
    public static string AppendToList(string columnName, object value)
        => $"{columnName} = {columnName} + [?]";

    /// <summary>
    /// Generates CQL for prepending to a list
    /// </summary>
    public static string PrependToList(string columnName, object value)
        => $"{columnName} = [?] + {columnName}";

    /// <summary>
    /// Generates CQL for removing from a list by value
    /// </summary>
    public static string RemoveFromList(string columnName, object value)
        => $"{columnName} = {columnName} - [?]";

    /// <summary>
    /// Generates CQL for adding to a set
    /// </summary>
    public static string AddToSet(string columnName, object value)
        => $"{columnName} = {columnName} + {{?}}";

    /// <summary>
    /// Generates CQL for removing from a set
    /// </summary>
    public static string RemoveFromSet(string columnName, object value)
        => $"{columnName} = {columnName} - {{?}}";

    /// <summary>
    /// Generates CQL for adding to a map
    /// </summary>
    public static string AddToMap(string columnName, object key, object value)
        => $"{columnName}[?] = ?";

    /// <summary>
    /// Generates CQL for removing from a map by key
    /// </summary>
    public static string RemoveFromMap(string columnName, object key)
        => $"DELETE {columnName}[?]";

    /// <summary>
    /// Generates CQL for updating an entire collection
    /// </summary>
    public static string UpdateCollection(string columnName, CollectionType collectionType)
        => $"{columnName} = ?";
}