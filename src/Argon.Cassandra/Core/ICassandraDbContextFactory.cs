namespace Argon.Cassandra.Core;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Defines a factory for creating <see cref="T:Argon.Cassandra.Core.DbContext" /> instances.
/// </summary>
/// <typeparam name="TContext">The <see cref="T:Argon.Cassandra.Core.DbContext" /> type to create.</typeparam>
public interface ICassandraDbContextFactory<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors |
                                DynamicallyAccessedMemberTypes.PublicProperties)]
    TContext>
    where TContext : CassandraDbContext
{
    /// <summary>
    ///     Creates a new <see cref="T:Argon.Cassandra.Core.DbContext" /> instance.
    /// </summary>
    /// <remarks>
    ///     The caller is responsible for disposing the context; it will not be disposed by any dependency injection container.
    /// </remarks>
    /// <returns>A new context instance.</returns>
    ICassandraDbContextScope<TContext> CreateDbContext();

    /// <summary>
    ///     Creates a new <see cref="T:Argon.Cassandra.Core.DbContext" /> instance in an async context.
    /// </summary>
    /// <remarks>
    ///     The caller is responsible for disposing the context; it will not be disposed by any dependency injection container.
    /// </remarks>
    /// <param name="cancellationToken">A <see cref="T:System.Threading.CancellationToken" /> to observe while waiting for the task to complete.</param>
    /// <returns>A task containing the created context that represents the asynchronous operation.</returns>
    /// <exception cref="T:System.OperationCanceledException">If the <see cref="T:System.Threading.CancellationToken" /> is canceled.</exception>
    Task<ICassandraDbContextScope<TContext>> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

public class CassandraDbContextFactory<T>(IServiceProvider serviceProvider) : ICassandraDbContextFactory<T> where T : CassandraDbContext
{
    public ICassandraDbContextScope<T> CreateDbContext()
    {
        var scope = serviceProvider.CreateAsyncScope();
        return new CassandraDbContextScope<T>(scope);
    }
}

public interface ICassandraDbContextScope<out T> : IAsyncDisposable where T : CassandraDbContext
{
    T Context { get; }
}

internal class CassandraDbContextScope<T>(AsyncServiceScope serviceProvider) : ICassandraDbContextScope<T> where T : CassandraDbContext
{
    public async ValueTask DisposeAsync()
        => await serviceProvider.DisposeAsync();

    public T Context { get; } = serviceProvider.ServiceProvider.GetRequiredService<T>();
}