namespace Argon.Cassandra.MaterializedViews;

/// <summary>
/// Registry for materialized views
/// </summary>
public class MaterializedViewRegistry
{
    private readonly Dictionary<Type, MaterializedViewMetadata> _viewMetadataCache = new();
    private readonly Dictionary<string, Type>                   _viewTypesByName   = new();

    /// <summary>
    /// Registers a materialized view type
    /// </summary>
    public void RegisterView<TView, TBaseEntity>() 
        where TView : class 
        where TBaseEntity : class
        => RegisterView(typeof(TView), typeof(TBaseEntity));

    /// <summary>
    /// Registers a materialized view type
    /// </summary>
    public void RegisterView(Type viewType, Type baseEntityType)
    {
        if (_viewMetadataCache.ContainsKey(viewType))
            return;

        var metadata = MaterializedViewMetadata.Create(viewType, baseEntityType);
        _viewMetadataCache[viewType]        = metadata;
        _viewTypesByName[metadata.ViewName] = viewType;
    }

    /// <summary>
    /// Gets view metadata for a type
    /// </summary>
    public MaterializedViewMetadata? GetViewMetadata(Type viewType)
        => _viewMetadataCache.TryGetValue(viewType, out var metadata) ? metadata : null;

    /// <summary>
    /// Gets view metadata by view name
    /// </summary>
    public MaterializedViewMetadata? GetViewMetadata(string viewName)
    {
        if (_viewTypesByName.TryGetValue(viewName, out var type))
        {
            return GetViewMetadata(type);
        }
        return null;
    }

    /// <summary>
    /// Checks if a type is a registered materialized view
    /// </summary>
    public bool IsView(Type type)
        => _viewMetadataCache.ContainsKey(type);

    /// <summary>
    /// Gets all registered view types
    /// </summary>
    public IEnumerable<Type> GetRegisteredViewTypes()
        => _viewMetadataCache.Keys;

    /// <summary>
    /// Clears all registered views
    /// </summary>
    public void Clear()
    {
        _viewMetadataCache.Clear();
        _viewTypesByName.Clear();
    }
}