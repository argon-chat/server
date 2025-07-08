namespace Argon.Cassandra.Collections;

/// <summary>
/// Helper for working with collection updates
/// </summary>
public class CollectionUpdateBuilder
{
    private readonly List<CollectionUpdate> updates = [];

    /// <summary>
    /// Adds an append operation to a list
    /// </summary>
    public CollectionUpdateBuilder AppendToList<T>(string propertyName, T value)
    {
        updates.Add(new CollectionUpdate
        {
            PropertyName = propertyName,
            Operation    = CollectionOperation.ListAppend,
            Value        = value
        });
        return this;
    }

    /// <summary>
    /// Adds a prepend operation to a list
    /// </summary>
    public CollectionUpdateBuilder PrependToList<T>(string propertyName, T value)
    {
        updates.Add(new CollectionUpdate
        {
            PropertyName = propertyName,
            Operation    = CollectionOperation.ListPrepend,
            Value        = value
        });
        return this;
    }

    /// <summary>
    /// Adds an element to a set
    /// </summary>
    public CollectionUpdateBuilder AddToSet<T>(string propertyName, T value)
    {
        updates.Add(new CollectionUpdate
        {
            PropertyName = propertyName,
            Operation    = CollectionOperation.SetAdd,
            Value        = value
        });
        return this;
    }

    /// <summary>
    /// Removes an element from a set
    /// </summary>
    public CollectionUpdateBuilder RemoveFromSet<T>(string propertyName, T value)
    {
        updates.Add(new CollectionUpdate
        {
            PropertyName = propertyName,
            Operation    = CollectionOperation.SetRemove,
            Value        = value
        });
        return this;
    }

    /// <summary>
    /// Adds or updates a map entry
    /// </summary>
    public CollectionUpdateBuilder PutInMap<TKey, TValue>(string propertyName, TKey key, TValue value)
    {
        updates.Add(new CollectionUpdate
        {
            PropertyName = propertyName,
            Operation    = CollectionOperation.MapPut,
            Key          = key,
            Value        = value
        });
        return this;
    }

    /// <summary>
    /// Removes a key from a map
    /// </summary>
    public CollectionUpdateBuilder RemoveFromMap<TKey>(string propertyName, TKey key)
    {
        updates.Add(new CollectionUpdate
        {
            PropertyName = propertyName,
            Operation    = CollectionOperation.MapRemove,
            Key          = key
        });
        return this;
    }

    /// <summary>
    /// Gets all collection updates
    /// </summary>
    public IReadOnlyList<CollectionUpdate> GetUpdates() => updates.AsReadOnly();

    /// <summary>
    /// Clears all updates
    /// </summary>
    public void Clear() => updates.Clear();
}