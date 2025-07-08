namespace Argon.Cassandra.Core;

using Extensions;
using Mapping;
using Microsoft.EntityFrameworkCore.Query;
using Query;

/// <summary>
/// Represents a collection of entities in a Cassandra database that can be queried.
/// </summary>
/// <typeparam name="T">The type of entity in the set.</typeparam>
public class CassandraDbSet<T> : IQueryable<T>, IAsyncQueryProvider, IAsyncEnumerable<T> where T : class
{
    private readonly CassandraDbContext        _context;
    private readonly CassandraQueryProvider<T> _queryProvider;

    /// <summary>
    /// Initializes a new instance of the CassandraDbSet class.
    /// </summary>
    /// <param name="context">The context this set belongs to.</param>
    public CassandraDbSet(CassandraDbContext context)
    {
        _context       = context;
        _queryProvider = new CassandraQueryProvider<T>(context);
        Expression    = Expression.Constant(this);
    }

    /// <summary>
    /// Gets the element type of the set.
    /// </summary>
    public Type ElementType => typeof(T);

    /// <summary>
    /// Gets the expression representing the set.
    /// </summary>
    public Expression Expression { get; }

    /// <summary>
    /// Gets the query provider for the set.
    /// </summary>
    public IQueryProvider Provider => _queryProvider;

    /// <summary>
    /// Returns an enumerator that iterates through the set.
    /// </summary>
    /// <returns>An enumerator that can be used to iterate through the set.</returns>
    public IEnumerator<T> GetEnumerator()
        => _queryProvider.Execute<IEnumerable<T>>(Expression).GetEnumerator();

    /// <summary>
    /// Returns an enumerator that iterates through the set.
    /// </summary>
    /// <returns>An enumerator that can be used to iterate through the set.</returns>
    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    /// <summary>
    /// Returns an async enumerator that asynchronously iterates through the set.
    /// </summary>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>An async enumerator that can be used to asynchronously iterate through the set.</returns>
    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var entities = await _queryProvider.DoExecuteAsync<IEnumerable<T>>(Expression, cancellationToken);
        foreach (var item in entities)
            yield return item;
    }

    /// <summary>
    /// Adds an entity to the set.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    public void Add(T entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        _context.TrackEntity(entity, EntityState.Added);
    }

    /// <summary>
    /// Adds an entity to the set.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    /// <param name="ttl">Time to live</param>
    public void Add(T entity, int ttl)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));
        if (entity is IWithTTL withTtl)
            withTtl.TtlSeconds = ttl;
        _context.TrackEntity(entity, EntityState.Added);
    }

    /// <summary>
    /// Asynchronously adds an entity to the set.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public Task AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        Add(entity);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asynchronously adds an entity to the set.
    /// </summary>
    /// <param name="entity">The entity to add.</param>
    /// <param name="ttl">Time to live</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public Task AddAsync(T entity, int ttl, CancellationToken cancellationToken = default)
    {
        Add(entity, ttl);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds multiple entities to the set.
    /// </summary>
    /// <param name="entities">The entities to add.</param>
    public void AddRange(params T[] entities)
        => AddRange((IEnumerable<T>)entities);

    /// <summary>
    /// Adds multiple entities to the set.
    /// </summary>
    /// <param name="entities">The entities to add.</param>
    public void AddRange(IEnumerable<T> entities)
    {
        if (entities == null) throw new ArgumentNullException(nameof(entities));

        foreach (var entity in entities)
            Add(entity);
    }

    /// <summary>
    /// Asynchronously adds multiple entities to the set.
    /// </summary>
    /// <param name="entities">The entities to add.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
    {
        AddRange(entities);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates an entity in the set.
    /// </summary>
    /// <param name="entity">The entity to update.</param>
    public void Update(T entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var currentState = _context.GetEntityState(entity);
        if (currentState is EntityState.Detached or not EntityState.Added)
            _context.TrackEntity(entity, EntityState.Modified);
    }

    /// <summary>
    /// Updates multiple entities in the set.
    /// </summary>
    /// <param name="entities">The entities to update.</param>
    public void UpdateRange(params T[] entities)
        => UpdateRange((IEnumerable<T>)entities);

    /// <summary>
    /// Updates multiple entities in the set.
    /// </summary>
    /// <param name="entities">The entities to update.</param>
    public void UpdateRange(IEnumerable<T> entities)
    {
        if (entities == null) throw new ArgumentNullException(nameof(entities));

        foreach (var entity in entities)
            Update(entity);
    }

    /// <summary>
    /// Removes an entity from the set.
    /// </summary>
    /// <param name="entity">The entity to remove.</param>
    public void Remove(T entity)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var currentState = _context.GetEntityState(entity);
        _context.TrackEntity(entity, currentState == EntityState.Added ? EntityState.Detached : EntityState.Deleted);
    }

    /// <summary>
    /// Removes multiple entities from the set.
    /// </summary>
    /// <param name="entities">The entities to remove.</param>
    public void RemoveRange(params T[] entities)
        => RemoveRange((IEnumerable<T>)entities);

    /// <summary>
    /// Removes multiple entities from the set.
    /// </summary>
    /// <param name="entities">The entities to remove.</param>
    public void RemoveRange(IEnumerable<T> entities)
    {
        if (entities == null) throw new ArgumentNullException(nameof(entities));

        foreach (var entity in entities)
            Remove(entity);
    }

    /// <summary>
    /// Finds an entity with the given primary key values.
    /// </summary>
    /// <param name="keyValues">The values of the primary key.</param>
    /// <returns>The found entity or null if not found.</returns>
    public T? Find(params object[] keyValues)
    {
        if (keyValues == null || keyValues.Length == 0)
            throw new ArgumentException("Key values cannot be null or empty", nameof(keyValues));

        var metadata             = EntityMetadataCache.GetMetadata<T>();
        var primaryKeyProperties = metadata.PartitionKeys.Concat(metadata.ClusteringKeys).ToList();

        if (keyValues.Length != primaryKeyProperties.Count)
            throw new ArgumentException($"Expected {primaryKeyProperties.Count} key values, but got {keyValues.Length}", nameof(keyValues));

        // Build WHERE clause for primary key
        var whereConditions = primaryKeyProperties.Select((prop, index) =>
            $"{metadata.GetColumnName(prop)} = {{{index}}}").ToList();
        var whereClause = string.Join(" AND ", whereConditions);

        var cql = $"SELECT * FROM {metadata.GetFullTableName()} WHERE {whereClause}";

        var result = _context.ExecuteCql(cql, keyValues);
        var row    = result.FirstOrDefault();

        return row != null ? MapRowToEntity(row, metadata) : null;
    }

    /// <summary>
    /// Asynchronously finds an entity with the given primary key values.
    /// </summary>
    /// <param name="keyValues">The values of the primary key.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the found entity or null if not found.</returns>
    public Task<T?> FindAsync(params object[] keyValues)
        => FindAsync(keyValues, CancellationToken.None);

    /// <summary>
    /// Asynchronously finds an entity with the given primary key values.
    /// </summary>
    /// <param name="keyValues">The values of the primary key.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the found entity or null if not found.</returns>
    public async Task<T?> FindAsync(object[] keyValues, CancellationToken cancellationToken)
    {
        if (keyValues == null || keyValues.Length == 0)
            throw new ArgumentException("Key values cannot be null or empty", nameof(keyValues));

        var metadata             = EntityMetadataCache.GetMetadata<T>();
        var primaryKeyProperties = metadata.PartitionKeys.Concat(metadata.ClusteringKeys).ToList();

        if (keyValues.Length != primaryKeyProperties.Count)
            throw new ArgumentException($"Expected {primaryKeyProperties.Count} key values, but got {keyValues.Length}", nameof(keyValues));

        // Build WHERE clause for primary key
        var whereConditions = primaryKeyProperties.Select((prop, index) =>
            $"{metadata.GetColumnName(prop)} = {{{index}}}").ToList();
        var whereClause = string.Join(" AND ", whereConditions);

        var cql = $"SELECT * FROM {metadata.GetFullTableName()} WHERE {whereClause}";

        var result = await _context.ExecuteCqlAsync(cql, keyValues).ConfigureAwait(false);
        var row    = result.FirstOrDefault();

        return row != null ? MapRowToEntity(row, metadata) : null;
    }

    /// <summary>
    /// Executes a raw CQL query and returns the results.
    /// </summary>
    /// <param name="cql">The CQL query to execute.</param>
    /// <param name="parameters">The parameters for the query.</param>
    /// <returns>The query results as entities.</returns>
    public IEnumerable<T> FromCql(string cql, params object[] parameters)
    {
        var result   = _context.ExecuteCql(cql, parameters);
        var metadata = EntityMetadataCache.GetMetadata<T>();

        foreach (var row in result)
            yield return MapRowToEntity(row, metadata);
    }

    /// <summary>
    /// Asynchronously executes a raw CQL query and returns the results.
    /// </summary>
    /// <param name="cql">The CQL query to execute.</param>
    /// <param name="parameters">The parameters for the query.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the query results as entities.</returns>
    public async Task<IEnumerable<T>> FromCqlAsync(string cql, params object[] parameters)
    {
        var result   = await _context.ExecuteCqlAsync(cql, parameters).ConfigureAwait(false);
        var metadata = EntityMetadataCache.GetMetadata<T>();

        return result.Select(row => MapRowToEntity(row, metadata)).ToList();
    }

    private T MapRowToEntity(Row row, EntityMetadata metadata)
    {
        var entity = Activator.CreateInstance<T>();

        foreach (var property in metadata.Properties)
        {
            var columnName = metadata.GetColumnName(property);

            try
            {
                if (row.IsNull(columnName))
                    continue;

                if (metadata.Converters.TryGetValue(property, out var converter))
                {
                    var rawValue = row.GetValue(converter.ToType, columnName);
                    var convertedValue = converter.BoxedConvertFrom(rawValue);
                    property.SetValue(entity, convertedValue);
                }
                else
                {
                    var value = row.GetValue(property.PropertyType, columnName);
                    property.SetValue(entity, value);
                }
            }
            catch (ArgumentException)
            {
            }
        }

        _context.TrackEntity(entity, EntityState.Unchanged);
        return entity;
    }


    #region IAsyncQuery

    public TResult Execute<TResult>(Expression expression)
        => ((IQueryProvider)_queryProvider).Execute<TResult>(expression);

    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = new())
        => ((IQueryProvider)_queryProvider).Execute<TResult>(expression);

    public object? Execute(Expression expression)
        => ((IQueryProvider)_queryProvider).Execute(expression);

    public IQueryable CreateQuery(Expression expression)
        => ((IQueryProvider)_queryProvider).CreateQuery(expression);

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
        => ((IQueryProvider)_queryProvider).CreateQuery<TElement>(expression);

    #endregion
}