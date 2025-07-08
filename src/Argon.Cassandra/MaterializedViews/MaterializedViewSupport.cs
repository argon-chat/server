using Argon.Cassandra.Mapping;

namespace Argon.Cassandra.MaterializedViews;

using Mapping;

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

/// <summary>
/// Specifies the WHERE clause for a materialized view
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class MaterializedViewWhereAttribute(string whereClause) : Attribute
{
    public string WhereClause { get; } = whereClause ?? throw new ArgumentNullException(nameof(whereClause));
}

/// <summary>
/// Metadata for Materialized Views
/// </summary>
public class MaterializedViewMetadata
{
    public string ViewName { get; set; } = string.Empty;
    public string BaseTable { get; set; } = string.Empty;
    public string? Keyspace { get; set; }
    public string? WhereClause { get; set; }
    public Type ViewType { get; set; } = null!;
    public Type BaseEntityType { get; set; } = null!;
    public List<MaterializedViewColumn> Columns { get; set; } = new();
    public List<MaterializedViewColumn> PartitionKeys { get; set; } = new();
    public List<MaterializedViewColumn> ClusteringKeys { get; set; } = new();

    public static MaterializedViewMetadata Create(Type viewType, Type baseEntityType)
    {
        var mvAttribute = viewType.GetCustomAttribute<MaterializedViewAttribute>();
        if (mvAttribute == null)
        {
            throw new InvalidOperationException($"Type {viewType.Name} must be marked with [MaterializedView] attribute.");
        }

        var whereAttribute = viewType.GetCustomAttribute<MaterializedViewWhereAttribute>();

        var metadata = new MaterializedViewMetadata
        {
            ViewName = mvAttribute.ViewName,
            BaseTable = mvAttribute.BaseTable,
            Keyspace = mvAttribute.Keyspace,
            WhereClause = whereAttribute?.WhereClause,
            ViewType = viewType,
            BaseEntityType = baseEntityType
        };        // Get all properties that can be mapped
        var properties = viewType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && !p.GetCustomAttributes<NotMappedAttribute>().Any());

        foreach (var property in properties)
        {
            var column = new MaterializedViewColumn
            {
                PropertyName = property.Name,
                ColumnName = GetColumnName(property),
                PropertyType = property.PropertyType,
                PropertyInfo = property
            };

            metadata.Columns.Add(column);

            // Check for partition key
            if (property.GetCustomAttribute<PartitionKeyAttribute>() != null)
            {
                var partitionKey = property.GetCustomAttribute<PartitionKeyAttribute>()!;
                column.IsPartitionKey = true;
                column.Order = partitionKey.Order;
                metadata.PartitionKeys.Add(column);
            }

            // Check for clustering key            if (property.GetCustomAttribute<ClusteringKeyAttribute>() != null)
            {
                var clusteringKey = property.GetCustomAttribute<ClusteringKeyAttribute>()!;
                column.IsClusteringKey = true;
                column.Order = clusteringKey.Order;
                column.SortOrder = clusteringKey.Descending ? SortOrder.Descending : SortOrder.Ascending;
                metadata.ClusteringKeys.Add(column);
            }
        }

        // Sort keys by order
        metadata.PartitionKeys = metadata.PartitionKeys.OrderBy(k => k.Order).ToList();
        metadata.ClusteringKeys = metadata.ClusteringKeys.OrderBy(k => k.Order).ToList();

        return metadata;
    }

    private static string GetColumnName(PropertyInfo property)
    {        var columnAttribute = property.GetCustomAttribute<ColumnAttribute>();
        return columnAttribute?.Name ?? property.Name.ToLowerInvariant();
    }
}

/// <summary>
/// Column metadata for materialized views
/// </summary>
public class MaterializedViewColumn
{
    public string PropertyName { get; set; } = string.Empty;
    public string ColumnName { get; set; } = string.Empty;
    public Type PropertyType { get; set; } = null!;
    public PropertyInfo PropertyInfo { get; set; } = null!;
    public bool IsPartitionKey { get; set; }
    public bool IsClusteringKey { get; set; }
    public int Order { get; set; }
    public SortOrder SortOrder { get; set; } = SortOrder.Ascending;
}

/// <summary>
/// Builder for creating materialized views
/// </summary>
public class MaterializedViewBuilder(string viewName, string baseTable)
{
    private readonly string       _viewName  = viewName ?? throw new ArgumentNullException(nameof(viewName));
    private readonly string       _baseTable = baseTable ?? throw new ArgumentNullException(nameof(baseTable));
    private          string?      _keyspace;
    private          string?      _whereClause;
    private readonly List<string> _selectedColumns = new();
    private readonly List<string> _partitionKeys   = new();
    private readonly List<string> _clusteringKeys  = new();

    /// <summary>
    /// Sets the keyspace for the view
    /// </summary>
    public MaterializedViewBuilder InKeyspace(string keyspace)
    {
        _keyspace = keyspace;
        return this;
    }

    /// <summary>
    /// Adds columns to select
    /// </summary>
    public MaterializedViewBuilder SelectColumns(params string[] columns)
    {
        _selectedColumns.AddRange(columns);
        return this;
    }

    /// <summary>
    /// Sets the WHERE clause
    /// </summary>
    public MaterializedViewBuilder Where(string whereClause)
    {
        _whereClause = whereClause;
        return this;
    }

    /// <summary>
    /// Sets partition keys
    /// </summary>
    public MaterializedViewBuilder WithPartitionKeys(params string[] keys)
    {
        _partitionKeys.AddRange(keys);
        return this;
    }

    /// <summary>
    /// Sets clustering keys
    /// </summary>
    public MaterializedViewBuilder WithClusteringKeys(params string[] keys)
    {
        _clusteringKeys.AddRange(keys);
        return this;
    }

    /// <summary>
    /// Builds the CREATE MATERIALIZED VIEW CQL statement
    /// </summary>
    public string BuildCreateCql()
    {
        var cql = new System.Text.StringBuilder();
        
        cql.Append("CREATE MATERIALIZED VIEW ");
        if (!string.IsNullOrEmpty(_keyspace))
        {
            cql.Append($"{_keyspace}.{_viewName}");
        }
        else
        {
            cql.Append(_viewName);
        }

        cql.Append(" AS SELECT ");
        if (_selectedColumns.Any())
        {
            cql.Append(string.Join(", ", _selectedColumns));
        }
        else
        {
            cql.Append("*");
        }

        cql.Append($" FROM {_baseTable}");

        if (!string.IsNullOrEmpty(_whereClause))
        {
            cql.Append($" WHERE {_whereClause}");
        }

        // Add primary key definition
        if (_partitionKeys.Any() || _clusteringKeys.Any())
        {
            cql.Append(" PRIMARY KEY (");
            
            if (_partitionKeys.Any())
            {
                if (_partitionKeys.Count == 1)
                {
                    cql.Append(_partitionKeys[0]);
                }
                else
                {
                    cql.Append($"({string.Join(", ", _partitionKeys)})");
                }

                if (_clusteringKeys.Any())
                {
                    cql.Append($", {string.Join(", ", _clusteringKeys)}");
                }
            }
            else if (_clusteringKeys.Any())
            {
                cql.Append(string.Join(", ", _clusteringKeys));
            }

            cql.Append(")");
        }

        cql.Append(";");
        return cql.ToString();
    }

    /// <summary>
    /// Builds the DROP MATERIALIZED VIEW CQL statement
    /// </summary>
    public string BuildDropCql()
    {
        var viewNameWithKeyspace = !string.IsNullOrEmpty(_keyspace) 
            ? $"{_keyspace}.{_viewName}" 
            : _viewName;
        
        return $"DROP MATERIALIZED VIEW IF EXISTS {viewNameWithKeyspace};";
    }
}

/// <summary>
/// Registry for materialized views
/// </summary>
public class MaterializedViewRegistry
{
    private readonly Dictionary<Type, MaterializedViewMetadata> _viewMetadataCache = new();
    private readonly Dictionary<string, Type> _viewTypesByName = new();

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
        _viewMetadataCache[viewType] = metadata;
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

/// <summary>
/// Manager for materialized view operations
/// </summary>
public class MaterializedViewManager(MaterializedViewRegistry registry, ISession session)
{
    private readonly MaterializedViewRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly ISession                 _session  = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>
    /// Creates all registered materialized views
    /// </summary>
    public async Task CreateAllViewsAsync()
    {
        foreach (var viewType in _registry.GetRegisteredViewTypes())
        {
            await CreateViewAsync(viewType);
        }
    }

    /// <summary>
    /// Creates a specific materialized view
    /// </summary>
    public async Task CreateViewAsync<T>() where T : class
        => await CreateViewAsync(typeof(T));

    /// <summary>
    /// Creates a specific materialized view
    /// </summary>
    public async Task CreateViewAsync(Type viewType)
    {
        var metadata = _registry.GetViewMetadata(viewType);
        if (metadata == null)
        {
            throw new InvalidOperationException($"View type {viewType.Name} is not registered.");
        }

        var builder = new MaterializedViewBuilder(metadata.ViewName, metadata.BaseTable)
            .InKeyspace(metadata.Keyspace)
            .Where(metadata.WhereClause ?? "")
            .WithPartitionKeys(metadata.PartitionKeys.Select(pk => pk.ColumnName).ToArray())
            .WithClusteringKeys(metadata.ClusteringKeys.Select(ck => ck.ColumnName).ToArray());

        var createCql = builder.BuildCreateCql();
        await _session.ExecuteAsync(new SimpleStatement(createCql));
    }

    /// <summary>
    /// Drops a materialized view
    /// </summary>
    public async Task DropViewAsync<T>() where T : class
        => await DropViewAsync(typeof(T));

    /// <summary>
    /// Drops a materialized view
    /// </summary>
    public async Task DropViewAsync(Type viewType)
    {
        var metadata = _registry.GetViewMetadata(viewType);
        if (metadata == null)
        {
            throw new InvalidOperationException($"View type {viewType.Name} is not registered.");
        }

        var builder = new MaterializedViewBuilder(metadata.ViewName, metadata.BaseTable)
            .InKeyspace(metadata.Keyspace);

        var dropCql = builder.BuildDropCql();
        await _session.ExecuteAsync(new SimpleStatement(dropCql));
    }

    /// <summary>
    /// Refreshes a materialized view (drops and recreates)
    /// </summary>
    public async Task RefreshViewAsync<T>() where T : class
    {        await DropViewAsync<T>();
        await CreateViewAsync<T>();
    }
}
