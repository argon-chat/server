namespace Argon.Cassandra.Extensions;

/// <summary>
/// Extension methods for IQueryable to add Cassandra-specific operations.
/// </summary>
public static class CassandraQueryableExtensions
{
    /// <summary>
    /// Allows filtering on non-key columns (equivalent to ALLOW FILTERING in CQL).
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to add filtering to.</param>
    /// <returns>The queryable with filtering allowed.</returns>
    public static IQueryable<T> AllowFiltering<T>(this IQueryable<T> queryable) where T : class
    // This would be implemented in the query provider to add ALLOW FILTERING
    // For now, return the queryable as-is
        => queryable;

    /// <summary>
    /// Limits the query to a specific token range for pagination.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to add token filtering to.</param>
    /// <param name="startToken">The start token.</param>
    /// <param name="endToken">The end token.</param>
    /// <returns>The queryable with token filtering.</returns>
    public static IQueryable<T> TokenRange<T>(this IQueryable<T> queryable, long startToken, long endToken) where T : class
    // This would be implemented in the query provider to add token() filtering
    // For now, return the queryable as-is
        => queryable;

    /// <summary>
    /// Executes the query asynchronously and returns the first element, or a default value if no element is found.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the first element or default value.</returns>
    public async static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> queryable, CancellationToken cancellationToken = default) where T : class
    {
        if (queryable is Query.CassandraQuery<T> cassandraQuery)
        {
            return await cassandraQuery.FirstOrDefaultAsync(cancellationToken);
        }

        if (queryable is IAsyncEnumerable<T> asyncEnumerable)
        {
            await using var enumerator = asyncEnumerable.GetAsyncEnumerator(cancellationToken);
            return await enumerator.MoveNextAsync() ? enumerator.Current : default;
        }

        return queryable.FirstOrDefault();
    }    /// <summary>
    /// Executes the query asynchronously and returns the single element, or a default value if no element is found.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the single element or default value.</returns>
    public async static Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> queryable, CancellationToken cancellationToken = default) where T : class
    {
        if (queryable is Query.CassandraQuery<T> cassandraQuery)
        {
            return await cassandraQuery.SingleOrDefaultAsync(cancellationToken);
        }

        if (queryable is IAsyncEnumerable<T> asyncEnumerable)
        {
            var items = new List<T>();
            await foreach (var item in asyncEnumerable.WithCancellation(cancellationToken))
            {
                items.Add(item);
                if (items.Count > 1)
                    throw new InvalidOperationException("Sequence contains more than one element");
            }
            return items.SingleOrDefault();
        }

        return queryable.SingleOrDefault();
    }    /// <summary>
    /// Executes the query asynchronously and returns all results as a list.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains all results as a list.</returns>
    public async static Task<List<T>> ToListAsync<T>(this IQueryable<T> queryable, CancellationToken cancellationToken = default) where T : class
    {
        if (queryable is Query.CassandraQuery<T> cassandraQuery)
        {
            return await cassandraQuery.ToListAsync(cancellationToken);
        }

        if (queryable is IAsyncEnumerable<T> asyncEnumerable)
        {
            var items = new List<T>();
            await foreach (var item in asyncEnumerable.WithCancellation(cancellationToken))
            {
                items.Add(item);
            }
            return items;
        }

        return queryable.ToList();
    }    /// <summary>
    /// Executes the query asynchronously and returns the count of elements.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the count of elements.</returns>
    public async static Task<int> CountAsync<T>(this IQueryable<T> queryable, CancellationToken cancellationToken = default) where T : class
    {
        if (queryable is Query.CassandraQuery<T> cassandraQuery)
        {
            return await cassandraQuery.CountAsync(cancellationToken);
        }

        if (queryable is IAsyncEnumerable<T> asyncEnumerable)
        {
            var count = 0;
            await foreach (var _ in asyncEnumerable.WithCancellation(cancellationToken))
            {
                count++;
            }
            return count;
        }

        return queryable.Count();
    }    /// <summary>
    /// Executes the query asynchronously and returns whether any elements exist.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains whether any elements exist.</returns>
    public async static Task<bool> AnyAsync<T>(this IQueryable<T> queryable, CancellationToken cancellationToken = default) where T : class
    {
        if (queryable is Query.CassandraQuery<T> cassandraQuery)
            return await cassandraQuery.AnyAsync(cancellationToken);

        if (queryable is IAsyncEnumerable<T> asyncEnumerable)
        {
            await using var enumerator = asyncEnumerable.GetAsyncEnumerator(cancellationToken);
            return await enumerator.MoveNextAsync();
        }

        return queryable.Any();
    }

    /// <summary>
    /// Executes the query asynchronously and returns whether any elements satisfy a condition.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="predicate">A function to test each element for a condition.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains whether any elements satisfy the condition.</returns>
    public async static Task<bool> AnyAsync<T>(this IQueryable<T> queryable, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class
    {
        var filteredQuery = queryable.Where(predicate);
        return await AnyAsync(filteredQuery, cancellationToken);
    }

    /// <summary>
    /// Executes the query asynchronously and returns the first element.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the first element.</returns>
    public async static Task<T> FirstAsync<T>(this IQueryable<T> queryable, CancellationToken cancellationToken = default) where T : class
    {
        var result = await FirstOrDefaultAsync(queryable, cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Sequence contains no elements");
        return result;
    }

    /// <summary>
    /// Executes the query asynchronously and returns the first element that satisfies a condition.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="predicate">A function to test each element for a condition.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the first element that satisfies the condition.</returns>
    public async static Task<T> FirstAsync<T>(this IQueryable<T> queryable, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class
    {
        var filteredQuery = queryable.Where(predicate);
        return await FirstAsync(filteredQuery, cancellationToken);
    }

    /// <summary>
    /// Executes the query asynchronously and returns the first element that satisfies a condition, or a default value if no such element is found.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="predicate">A function to test each element for a condition.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the first element or default value.</returns>
    public async static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> queryable, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class
    {
        var filteredQuery = queryable.Where(predicate);
        return await FirstOrDefaultAsync(filteredQuery, cancellationToken);
    }

    /// <summary>
    /// Executes the query asynchronously and returns the single element.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the single element.</returns>
    public async static Task<T> SingleAsync<T>(this IQueryable<T> queryable, CancellationToken cancellationToken = default) where T : class
    {
        var result = await SingleOrDefaultAsync(queryable, cancellationToken);
        if (result == null)
            throw new InvalidOperationException("Sequence contains no elements");
        return result;
    }

    /// <summary>
    /// Executes the query asynchronously and returns all results as an array.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable to execute.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains all results as an array.</returns>
    public async static Task<T[]> ToArrayAsync<T>(this IQueryable<T> queryable, CancellationToken cancellationToken = default) where T : class
    {
        var list = await ToListAsync(queryable, cancellationToken);
        return list.ToArray();
    }

    /// <summary>
    /// Provides async enumerable support for IQueryable.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="queryable">The queryable source.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>An async enumerable.</returns>
    public async static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IQueryable<T> queryable, [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        if (queryable is IAsyncEnumerable<T> asyncEnumerable)
        {
            await foreach (var item in asyncEnumerable.WithCancellation(cancellationToken))
            {
                yield return item;
            }
        }
        else
        {
            // Fallback to ToListAsync and iterate
            var list = await ToListAsync(queryable, cancellationToken);
            foreach (var item in list)
            {
                yield return item;
            }
        }
    }
}