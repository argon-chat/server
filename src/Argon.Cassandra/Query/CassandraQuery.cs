namespace Argon.Cassandra.Query;

public class CassandraQuery<T>(CassandraQueryProvider<T> provider, Expression expression) : IOrderedQueryable<T>, IAsyncEnumerable<T>
    where T : class
{
    public Type ElementType => typeof(T);

    public Expression Expression => expression;

    public IQueryProvider Provider => provider;

    public async Task<List<T>> ToListAsync(CancellationToken cancellationToken = default)
        => (await provider.DoExecuteAsync<IEnumerable<T>>(expression, cancellationToken)).ToList();

    public async Task<T?> FirstOrDefaultAsync(CancellationToken cancellationToken = default)
        => (await provider.DoExecuteAsync<IEnumerable<T>>(expression, cancellationToken)).FirstOrDefault();

    public async Task<T> FirstAsync(CancellationToken cancellationToken = default)
        => await FirstOrDefaultAsync(cancellationToken) ?? throw new InvalidOperationException("Sequence contains no elements");

    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default)
        => await FirstOrDefaultAsync(cancellationToken) is not null;

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
        => (await provider.DoExecuteAsync<IEnumerable<T>>(expression, cancellationToken)).Count();

    public async Task<T[]> ToArrayAsync(CancellationToken cancellationToken = default)
        => (await ToListAsync(cancellationToken)).ToArray();

    public async Task<T?> SingleOrDefaultAsync(CancellationToken cancellationToken = default)
        => (await provider.DoExecuteAsync<IEnumerable<T>>(expression, cancellationToken)).SingleOrDefault();

    public async Task<T> SingleAsync(CancellationToken cancellationToken = default)
        => (await SingleOrDefaultAsync(cancellationToken)) ?? throw new InvalidOperationException("Sequence contains no elements");

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
        => provider.Execute<IEnumerable<T>>(expression).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    /// <inheritdoc />
    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var entities = await provider.DoExecuteAsync<IEnumerable<T>>(expression, cancellationToken);
        foreach (var entity in entities)
            yield return entity;
    }
}
