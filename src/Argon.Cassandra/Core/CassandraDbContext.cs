namespace Argon.Cassandra.Core;

using Argon.Cassandra.Query;
using Collections;
using Configuration;
using global::Cassandra.Serialization;
using Mapping;
using MaterializedViews;
using Microsoft.Extensions.DependencyInjection;
using static Mapping.EntityMetadataCache;

/// <summary>
/// The main entry point for interacting with a Cassandra database.
/// Similar to Entity Framework's DbContext.
/// </summary>
public abstract class CassandraDbContext : IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<Type, object>            entitySets = new();
    private readonly IServiceProvider                              _serviceProvider;
    private readonly ILogger<CassandraDbContext>?                  logger;
    private readonly ConcurrentDictionary<object, EntityStateInfo> entityStates = new();
    private          Cluster?                                      cluster;
    private          ISession?                                     session;
    private          bool                                          disposed;

    /// <summary>
    /// Gets the Cassandra session used by this context.
    /// </summary>
    public ISession Session => GetSession();

    /// <summary>
    /// Gets the materialized view registry for managing views.
    /// </summary>
    public MaterializedViewRegistry ViewRegistry { get; } = new();

    /// <summary>
    /// Gets the collection update builder for batch collection operations.
    /// </summary>
    public CollectionUpdateBuilder CollectionUpdates { get; } = new();

    /// <summary>
    /// Gets the configuration used by this context.
    /// </summary>
    protected CassandraConfiguration Configuration { get; }

    /// <summary>
    /// Initializes a new instance of the CassandraDbContext with the specified configuration.
    /// </summary>
    /// <param name="configuration">The configuration to use for this context.</param>
    /// <param name="serviceProvider"></param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    protected CassandraDbContext(CassandraConfiguration configuration, IServiceProvider serviceProvider, ILogger<CassandraDbContext> logger)
    {
        this.Configuration    = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this._serviceProvider = serviceProvider;
        this.logger           = logger;
        Initialize();
    }


    private void Initialize()
    {
        DoConfigureModels();
        // Find all CassandraDbSet<T> properties and create instances
        var dbSetProperties = GetType().GetProperties()
           .Where(p => p.PropertyType.IsGenericType &&
                       p.PropertyType.GetGenericTypeDefinition() == typeof(CassandraDbSet<>));

        foreach (var property in dbSetProperties)
        {
            var entityType = property.PropertyType.GetGenericArguments()[0];
            var dbSetType  = typeof(CassandraDbSet<>).MakeGenericType(entityType);
            var dbSet = ActivatorUtilities.CreateInstance(_serviceProvider, dbSetType, this) ??
                        throw new InvalidOperationException($"Cannot create CassandraDbSet");
            if (property.CanWrite)
                property.SetValue(this, dbSet);
            entitySets[entityType] = dbSet;
        }
    }

    private ISession GetSession()
    {
        if (session != null) return session;

        var builder = Cluster.Builder()
           .WithTypeSerializers(new TypeSerializerDefinitions().Define(new ULongAsBigIntSerializer()))
           .AddContactPoints(Configuration.ContactPoints)
           .WithPort(Configuration.Port);

        if (!string.IsNullOrEmpty(Configuration.Username) && !string.IsNullOrEmpty(Configuration.Password))
            builder.WithCredentials(Configuration.Username, Configuration.Password);

        if (Configuration.RetryPolicy != null)
            builder.WithRetryPolicy(Configuration.RetryPolicy);

        if (Configuration.LoadBalancingPolicy != null)
            builder.WithLoadBalancingPolicy(Configuration.LoadBalancingPolicy);

        builder.WithSocketOptions(new SocketOptions()
           .SetConnectTimeoutMillis(Configuration.ConnectionTimeout)
           .SetReadTimeoutMillis(Configuration.QueryTimeout));

        cluster = builder.Build();

        // First connect without keyspace to handle keyspace creation
        var tempSession = cluster.Connect();

        // Create keyspace if needed and configured to do so
        if (Configuration.AutoCreateKeyspace && !string.IsNullOrEmpty(Configuration.Keyspace))
        {
            try
            {
                var replicationStrategy = Configuration.UseNetworkTopologyStrategy
                    ? $"'class': 'NetworkTopologyStrategy', {string.Join(", ", Configuration.DataCenterReplicationFactors.Select(kv => $"'{kv.Key}': {kv.Value}"))}"
                    : $"'class': 'SimpleStrategy', 'replication_factor': {Configuration.ReplicationFactor}";

                var cql =
                    $$"""
                      CREATE KEYSPACE IF NOT EXISTS {{Configuration.Keyspace}}
                      WITH REPLICATION = { {{replicationStrategy}} }
                      """;

                tempSession.Execute(new SimpleStatement(cql));
                logger?.LogInformation("Created/verified keyspace: {Keyspace}", Configuration.Keyspace);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to create keyspace {Keyspace}, continuing anyway", Configuration.Keyspace);
            }
        }

        // Now connect to the specific keyspace or use the temporary session
        if (!string.IsNullOrEmpty(Configuration.Keyspace))
        {
            try
            {
                tempSession.Dispose(); // Clean up temporary session
                session = cluster.Connect(Configuration.Keyspace);
                logger?.LogInformation("Connected to Cassandra cluster with keyspace: {Keyspace}", Configuration.Keyspace);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to connect to keyspace {Keyspace}, falling back to default connection", Configuration.Keyspace);
                session = cluster.Connect(); // Fallback to no keyspace
            }
        }
        else
        {
            session = tempSession; // Use the temporary session
            logger?.LogInformation("Connected to Cassandra cluster without specific keyspace");
        }

        return session;
    }

    /// <summary>
    /// Gets a DbSet for the specified entity type.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <returns>The DbSet for the entity type.</returns>
    public CassandraDbSet<T> Set<T>() where T : class
    {
        var entityType = typeof(T);

        if (entitySets.TryGetValue(entityType, out var existingSet))
        {
            return (CassandraDbSet<T>)existingSet;
        }

        var dbSet = new CassandraDbSet<T>(this, _serviceProvider.GetRequiredService<ILogger<CassandraQueryProvider<T>>>());
        entitySets[entityType] = dbSet;
        return dbSet;
    }

    /// <summary>
    /// Executes a raw CQL statement.
    /// </summary>
    /// <param name="cql">The CQL statement to execute.</param>
    /// <param name="parameters">The parameters for the statement.</param>
    /// <returns>The result set.</returns>
    public RowSet ExecuteCql(string cql, params object[] parameters)
    {
        logger?.LogDebug("Executing CQL: {Cql}", cql);

        if (parameters.Length <= 0)
            return Session.Execute(new SimpleStatement(cql));
        var prepared = Session.Prepare(cql);
        return Session.Execute(prepared.Bind(parameters));
    }

    /// <summary>
    /// Asynchronously executes a raw CQL statement.
    /// </summary>
    /// <param name="cql">The CQL statement to execute.</param>
    /// <param name="parameters">The parameters for the statement.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the result set.</returns>
    public async Task<RowSet> ExecuteCqlAsync(string cql, params object[] parameters)
    {
        logger?.LogDebug("Executing CQL async: {Cql}", cql);

        if (parameters.Length <= 0)
            return await Session.ExecuteAsync(new SimpleStatement(cql)).ConfigureAwait(false);
        var prepared = await Session.PrepareAsync(cql).ConfigureAwait(false);
        return await Session.ExecuteAsync(prepared.Bind(parameters)).ConfigureAwait(false);
    }

    /// <summary>
    /// Tracks an entity for the specified state.
    /// </summary>
    /// <param name="entity">The entity to track.</param>
    /// <param name="state">The state to track the entity in.</param>
    internal virtual void TrackEntity(object entity, EntityState state)
        => entityStates[entity] = new EntityStateInfo(state, null);

    /// <summary>
    /// Tracks an entity for the specified state.
    /// </summary>
    /// <param name="entity">The entity to track.</param>
    /// <param name="state">The state to track the entity in.</param>
    /// <param name="ttl">Time To Live</param>
    internal virtual void TrackEntity(object entity, EntityState state, int ttl)
        => entityStates[entity] = new EntityStateInfo(state, ttl);

    /// <summary>
    /// Gets the state of an entity.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The entity state.</returns>
    internal virtual EntityState GetEntityState(object entity)
        => entityStates.GetValueOrDefault(entity, new EntityStateInfo(EntityState.Detached));


    private static bool hasConfigured;

    private void DoConfigureModels()
    {
        if (Volatile.Read(ref hasConfigured)) return;
        Volatile.Write(ref hasConfigured, true);

        var ctx = new EntityMetadataContext(this);

        OnConfigureModels(ctx);

        ctx.Build();
    }

    protected virtual void OnConfigureModels(IEntityMetadataContext metadataContext)
    {
    }


    private BatchStatement GenerateBatchForSave(out int count)
    {
        var batch = new BatchStatement();
        var list  = entityStates.ToList();

        foreach (var (entity, state) in list)
            batch.Add(state switch
            {
                { state: EntityState.Added, TimeToLive: null } => GenerateInsertStatement(entity),
                { state: EntityState.Added, TimeToLive: > 0 }  => GenerateInsertStatement(entity, state.TimeToLive.Value),
                { state: EntityState.Modified }                => GenerateUpdateStatement(entity),
                { state: EntityState.Deleted }                 => GenerateDeleteStatement(entity),
                _                                              => throw new ArgumentOutOfRangeException()
            });

        count = list.Count;
        return batch;
    }

    /// <summary>
    /// Saves all changes made in this context to the database.
    /// </summary>
    /// <returns>The number of state entries written to the database.</returns>
    public virtual int SaveChanges()
    {
        var batch = GenerateBatchForSave(out var count);
        if (count > 0)
        {
            Session.Execute(batch);
            entityStates.Clear();
        }

        logger?.LogInformation("Saved {ChangeCount} changes to Cassandra", count);
        return count;
    }

    /// <summary>
    /// Asynchronously saves all changes made in this context to the database.
    /// </summary>
    /// <param name="cancellationToken">A CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous save operation. The task result contains the number of state entries written to the database.</returns>
    public virtual async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var batch = GenerateBatchForSave(out var count);
        if (count > 0)
        {
            await Session.ExecuteAsync(batch);
            entityStates.Clear();
        }

        logger?.LogInformation("Saved {ChangeCount} changes to Cassandra", count);
        return count;
    }

    private static SimpleStatement GenerateInsertStatement(object entity)
    {
        var metadata = GetMetadata(entity.GetType());
        var columns  = string.Join(", ", metadata.Properties.Select(p => metadata.GetColumnName(p)));
        var values   = string.Join(", ", metadata.Properties.Select((_, i) => $"?"));
        var cql      = $"INSERT INTO {metadata.GetFullTableName()} ({columns}) VALUES ({values})";

        var parameters = metadata.Properties.Select(p
            => metadata.Converters.TryGetValue(p, out var converter) ?
                converter.BoxedConvertTo(p.GetValue(entity)!) :
                p.GetValue(entity)).ToArray();
        return new SimpleStatement(cql, parameters);
    }

    private static SimpleStatement GenerateInsertStatement(object entity, int ttl)
    {
        var metadata = GetMetadata(entity.GetType());
        var columns  = string.Join(", ", metadata.Properties.Select(p => metadata.GetColumnName(p)));
        var values   = string.Join(", ", metadata.Properties.Select((_, _) => "?"));

        var ttlClause = string.Empty;

        if (ttl > 0)
            ttlClause = $"USING TTL {ttl} ";

        var cql        = $"INSERT INTO {metadata.GetFullTableName()} ({columns}) VALUES ({values}) {ttlClause}";
        var parameters = metadata.Properties.Select(p => p.GetValue(entity)).ToArray();
        return new SimpleStatement(cql, parameters);
    }

    private static SimpleStatement GenerateUpdateStatement(object entity)
    {
        var metadata = GetMetadata(entity.GetType());
        var nonKeyProperties = metadata.Properties
           .Except(metadata.PartitionKeys)
           .Except(metadata.ClusteringKeys)
           .ToList();

        var setClauses = string.Join(", ", nonKeyProperties.Select(p => $"{metadata.GetColumnName(p)} = ?"));
        var whereClause = string.Join(" AND ",
            metadata.PartitionKeys.Concat(metadata.ClusteringKeys)
               .Select(p => $"{metadata.GetColumnName(p)} = ?"));

        var cql = $"UPDATE {metadata.GetFullTableName()} SET {setClauses} WHERE {whereClause}";

        var setParameters   = nonKeyProperties.Select(p => p.GetValue(entity));
        var whereParameters = metadata.PartitionKeys.Concat(metadata.ClusteringKeys).Select(p => p.GetValue(entity));
        var parameters      = setParameters.Concat(whereParameters).ToArray();

        return new SimpleStatement(cql, parameters);
    }

    private static SimpleStatement GenerateDeleteStatement(object entity)
    {
        var metadata = GetMetadata(entity.GetType());
        var whereClause = string.Join(" AND ",
            metadata.PartitionKeys.Concat(metadata.ClusteringKeys)
               .Select(p => $"{metadata.GetColumnName(p)} = ?"));

        var cql        = $"DELETE FROM {metadata.GetFullTableName()} WHERE {whereClause}";
        var parameters = metadata.PartitionKeys.Concat(metadata.ClusteringKeys).Select(p => p.GetValue(entity)).ToArray();

        return new SimpleStatement(cql, parameters);
    }

    /// <summary>
    /// Creates the database schema if it doesn't exist.
    /// </summary>
    public virtual void EnsureCreated()
    {
        foreach (var entityType in entitySets.Keys)
            CreateTableIfNotExists(entityType);
    }

    /// <summary>
    /// Asynchronously creates the database schema if it doesn't exist.
    /// </summary>
    /// <param name="cancellationToken">A CancellationToken to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public virtual async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entityType in entitySets.Keys)
            await CreateTableIfNotExistsAsync(entityType).ConfigureAwait(false);
    }

    private void CreateTableIfNotExists(Type entityType)
    {
        var metadata = GetMetadata(entityType);
        var cql      = GenerateCreateTableCql(metadata);
        Session.Execute(new SimpleStatement(cql));
        logger?.LogInformation("Created table: {TableName}", metadata.GetFullTableName());
    }

    private async Task CreateTableIfNotExistsAsync(Type entityType)
    {
        var metadata = GetMetadata(entityType);
        var cql      = GenerateCreateTableCql(metadata);
        await Session.ExecuteAsync(new SimpleStatement(cql)).ConfigureAwait(false);
        logger?.LogInformation("Created table: {TableName}", metadata.GetFullTableName());
    }

    private string GenerateCreateTableCql(EntityMetadata metadata)
    {
        var columns = metadata.Properties.Select(p =>
        {
            var columnName = metadata.GetColumnName(p);
            var columnType = metadata.Converters.TryGetValue(p, out var converter)
                ? GetCassandraType(converter.ToType)
                : GetCassandraType(p.PropertyType);
            var isStatic = metadata.StaticColumns.Contains(p) ? " STATIC" : "";
            return $"{columnName} {columnType}{isStatic}";
        });

        var partitionKeyColumns = string.Join(", ", metadata.PartitionKeys.Select(metadata.GetColumnName));
        var clusteringKeyColumns = metadata.ClusteringKeys.Any()
            ? ", " + string.Join(", ", metadata.ClusteringKeys.Select(metadata.GetColumnName))
            : "";

        var primaryKey = metadata.PartitionKeys.Count == 1 && !metadata.ClusteringKeys.Any()
            ? partitionKeyColumns
            : $"({partitionKeyColumns}){clusteringKeyColumns}";

        var clusteringOrder = "";
        if (metadata.ClusteringKeys.Any())
        {
            var orderClauses = metadata.ClusteringKeys.Select(p =>
            {
                var columnName     = metadata.GetColumnName(p);
                var clusteringAttr = p.GetCustomAttribute<ClusteringKeyAttribute>()!;
                var order          = clusteringAttr.Descending ? "DESC" : "ASC";
                return $"{columnName} {order}";
            });
            clusteringOrder = $" WITH CLUSTERING ORDER BY ({string.Join(", ", orderClauses)})";
        }

        return $@"
            CREATE TABLE IF NOT EXISTS {metadata.GetFullTableName()} (
                {string.Join(",\n                ", columns)},
                PRIMARY KEY ({primaryKey})
            ){clusteringOrder}";
    }

    private string GetCassandraType(Type clrType)
    {
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(Nullable<>))
            clrType = Nullable.GetUnderlyingType(clrType)!;

        return clrType.Name switch
        {
            nameof(String)         => "text",
            nameof(Int32)          => "int",
            nameof(Int64)          => "bigint",
            nameof(Boolean)        => "boolean",
            nameof(Double)         => "double",
            nameof(Single)         => "float",
            nameof(Decimal)        => "decimal",
            nameof(Guid)           => "uuid",
            nameof(DateTime)       => "timestamp",
            nameof(DateTimeOffset) => "timestamp",
            nameof(TimeUuid)       => "timeuuid",
            nameof(Byte) + "[]"    => "blob",
            _                      => throw new InvalidOperationException($"Type '{clrType.FullName}' is not supported")
        };
    }

#region Advanced Features

    /// <summary>
    /// Registers a materialized view in the context.
    /// </summary>
    public void RegisterView<TView, TBaseEntity>()
        where TView : class
        where TBaseEntity : class
        => ViewRegistry.RegisterView<TView, TBaseEntity>();

    /// <summary>
    /// Creates all registered materialized views.
    /// </summary>
    public async Task CreateViewsAsync()
        => await new MaterializedViewManager(ViewRegistry, Session).CreateAllViewsAsync();

    /// <summary>
    /// Drops and recreates all registered materialized views.
    /// </summary>
    public async Task RefreshViewsAsync()
    {
        var viewManager = new MaterializedViewManager(ViewRegistry, Session);
        foreach (var viewType in ViewRegistry.GetRegisteredViewTypes())
            await (Task)typeof(MaterializedViewManager)
               .GetMethod("RefreshViewAsync")!
               .MakeGenericMethod(viewType)
               .Invoke(viewManager, null)!;
    }

    /// <summary>
    /// Executes collection updates using the collection update builder.
    /// </summary>
    public async Task<int> SaveCollectionChangesAsync<T>(T entity) where T : class
    {
        var updates = CollectionUpdates.GetUpdates();
        if (!updates.Any()) return 0;

        var metadata    = GetMetadata(typeof(T));
        var whereClause = GenerateWhereClauseForEntity(entity, metadata);
        var changeCount = 0;

        foreach (var update in updates)
        {
            var columnName = metadata.Properties
                                .FirstOrDefault(p => p.Name == update.PropertyName)?.Name?.ToLowerInvariant()
                             ?? update.PropertyName.ToLowerInvariant();

            var cql = update.Operation switch
            {
                CollectionOperation.ListAppend => $"UPDATE {metadata.GetFullTableName()} SET {columnName} = {columnName} + [?] WHERE {whereClause}",
                CollectionOperation.ListPrepend => $"UPDATE {metadata.GetFullTableName()} SET {columnName} = [?] + {columnName} WHERE {whereClause}",
                CollectionOperation.SetAdd => $"UPDATE {metadata.GetFullTableName()} SET {columnName} = {columnName} + {{?}} WHERE {whereClause}",
                CollectionOperation.SetRemove => $"UPDATE {metadata.GetFullTableName()} SET {columnName} = {columnName} - {{?}} WHERE {whereClause}",
                CollectionOperation.MapPut => $"UPDATE {metadata.GetFullTableName()} SET {columnName}[?] = ? WHERE {whereClause}",
                CollectionOperation.MapRemove => $"DELETE {columnName}[?] FROM {metadata.GetFullTableName()} WHERE {whereClause}",
                _ => throw new NotSupportedException($"Collection operation {update.Operation} is not supported.")
            };

            var parameters = new List<object>();
            switch (update.Operation)
            {
                case CollectionOperation.MapPut:
                    parameters.Add(update.Key!);
                    parameters.Add(update.Value!);
                    break;
                case CollectionOperation.MapRemove:
                    parameters.Add(update.Key!);
                    break;
                default:
                    parameters.Add(update.Value!);
                    break;
            }

            // Add WHERE clause parameters
            var keyValues = GetPrimaryKeyValues(entity, metadata);
            parameters.AddRange(keyValues);

            await Session.ExecuteAsync(new SimpleStatement(cql, parameters.ToArray()));
            changeCount++;
        }

        CollectionUpdates.Clear();
        return changeCount;
    }

    private string GenerateWhereClauseForEntity<T>(T entity, EntityMetadata metadata) where T : class
    {
        var keyProperties = metadata.PartitionKeys.Concat(metadata.ClusteringKeys);
        return string.Join(" AND ", keyProperties.Select(p => $"{metadata.GetColumnName(p)} = ?"));
    }

    private object[] GetPrimaryKeyValues<T>(T entity, EntityMetadata metadata) where T : class
    {
        var keyProperties = metadata.PartitionKeys.Concat(metadata.ClusteringKeys);
        return keyProperties.Select(p => p.GetValue(entity) ?? throw new InvalidOperationException($"Primary key property {p.Name} cannot be null"))
           .ToArray();
    }

    /// <summary>
    /// Gets a DbSet for a materialized view.
    /// </summary>
    public CassandraDbSet<TView> View<TView>() where TView : class
    {
        var viewType = typeof(TView);

        if (!ViewRegistry.IsView(viewType))
            throw new InvalidOperationException($"Type {viewType.Name} is not registered as a materialized view.");

        if (entitySets.TryGetValue(viewType, out var existingSet))
            return (CassandraDbSet<TView>)existingSet;

        var dbSet = new CassandraDbSet<TView>(this, _serviceProvider.GetRequiredService<ILogger<CassandraQueryProvider<TView>>>());
        entitySets[viewType] = dbSet;
        return dbSet;
    }

    /// <summary>
    /// Registers all UDTs and views from the current assembly.
    /// </summary>
    public void RegisterTypesFromAssembly(Assembly? assembly = null)
    {
        assembly ??= Assembly.GetCallingAssembly();

        // Register materialized views
        var viewTypes = assembly.GetTypes()
           .Where(t => t.GetCustomAttribute<MaterializedViewAttribute>() != null);

        foreach (var viewType in viewTypes)
        {
            // Find the base entity type (this is simplified - in real implementation would be more sophisticated)
            var baseTableName = viewType.GetCustomAttribute<MaterializedViewAttribute>()?.BaseTable;
            var baseEntityType = assembly.GetTypes()
               .FirstOrDefault(t => t.GetCustomAttribute<TableAttribute>()?.Name == baseTableName);

            if (baseEntityType != null)
                ViewRegistry.RegisterView(viewType, baseEntityType);
        }
    }

#endregion

    /// <summary>
    /// Releases the underlying Cassandra session and cluster.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by this context.
    /// </summary>
    /// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;
        if (disposing)
        {
            session?.Dispose();
            cluster?.Dispose();
            logger?.LogInformation("Disposed Cassandra context");
        }

        disposed = true;
    }

    /// <summary>
    /// Asynchronously releases the underlying Cassandra session and cluster.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Asynchronously releases the unmanaged resources used by this context.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (session != null)
            await session.ShutdownAsync().ConfigureAwait(false);

        // Cluster doesn't have an async disposal method
        cluster?.Dispose();
        logger?.LogInformation("Disposed Cassandra context async");
    }
}