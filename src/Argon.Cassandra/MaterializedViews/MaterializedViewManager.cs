namespace Argon.Cassandra.MaterializedViews;

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