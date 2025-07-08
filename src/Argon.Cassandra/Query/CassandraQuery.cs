using Argon.Cassandra.Core;

namespace Argon.Cassandra.Query;

/// <summary>
/// Represents a LINQ query against a Cassandra table
/// </summary>
/// <typeparam name="T">The entity type</typeparam>
public class CassandraQuery<T> : IQueryable<T>, IAsyncEnumerable<T> where T : class
{
    private readonly CassandraQueryProvider<T> _provider;
    private readonly Expression _expression;

    /// <summary>
    /// Initializes a new instance of the CassandraQuery class
    /// </summary>
    /// <param name="provider">The query provider</param>
    /// <param name="expression">The expression tree</param>
    public CassandraQuery(CassandraQueryProvider<T> provider, Expression expression)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _expression = expression ?? throw new ArgumentNullException(nameof(expression));
    }

    /// <summary>
    /// Gets the type of the element(s) that are returned when the expression tree associated with this instance of IQueryable is executed
    /// </summary>
    public Type ElementType => typeof(T);

    /// <summary>
    /// Gets the expression tree that is associated with the instance of IQueryable
    /// </summary>
    public Expression Expression => _expression;

    /// <summary>
    /// Gets the query provider that is associated with this data source
    /// </summary>
    public IQueryProvider Provider => _provider;

    /// <summary>
    /// Returns an enumerator that iterates through the collection
    /// </summary>
    /// <returns>An enumerator that can be used to iterate through the collection</returns>
    public IEnumerator<T> GetEnumerator()
        => _provider.Execute<IEnumerable<T>>(_expression).GetEnumerator();

    /// <summary>
    /// Returns an enumerator that iterates through a collection
    /// </summary>
    /// <returns>An IEnumerator object that can be used to iterate through the collection</returns>
    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    /// <summary>
    /// Returns an async enumerator that asynchronously iterates through the collection
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the operation</param>
    /// <returns>An async enumerator that can be used to asynchronously iterate through the collection</returns>
    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var entities = await _provider.DoExecuteAsync<IEnumerable<T>>(_expression, cancellationToken);
        foreach (var entity in entities)
        {
            yield return entity;
        }
    }

    /// <summary>
    /// Asynchronously creates a List from the query
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task<List<T>> ToListAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _provider.DoExecuteAsync<IEnumerable<T>>(_expression, cancellationToken);
        return entities.ToList();
    }

    /// <summary>
    /// Asynchronously returns the first element, or a default value if no element is found
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _provider.DoExecuteAsync<IEnumerable<T>>(_expression, cancellationToken);
        return entities.FirstOrDefault();
    }

    /// <summary>
    /// Asynchronously returns the first element
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task<T> FirstAsync(CancellationToken cancellationToken = default)
    {
        var result = await FirstOrDefaultAsync(cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Sequence contains no elements");
        return result;
    }

    /// <summary>
    /// Asynchronously determines whether the sequence contains any elements
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        var result = await FirstOrDefaultAsync(cancellationToken);
        return result != null;
    }

    /// <summary>
    /// Asynchronously returns the number of elements in the sequence
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _provider.DoExecuteAsync<IEnumerable<T>>(_expression, cancellationToken);
        return entities.Count();
    }

    /// <summary>
    /// Asynchronously creates an array from the query
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default)
    {
        var list = await ToListAsync(cancellationToken);
        return list.ToArray();
    }

    /// <summary>
    /// Asynchronously returns the single element, or a default value if no element is found
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _provider.DoExecuteAsync<IEnumerable<T>>(_expression, cancellationToken);
        return entities.SingleOrDefault();
    }

    /// <summary>
    /// Asynchronously returns the single element
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    public async Task<T> SingleAsync(CancellationToken cancellationToken = default)
    {
        var result = await SingleOrDefaultAsync(cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Sequence contains no elements");
        return result;
    }
}
