using Argon.Cassandra.Configuration;
using Argon.Cassandra.Core;
using Argon.Cassandra.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Argon.Cassandra.Extensions;

using Configuration;
using Core;
using Migrations;

/// <summary>
/// Extension methods for setting up CassandraORM services in an <see cref="IServiceCollection" />.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds CassandraORM services to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <typeparam name="TContext">The type of context to add.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="optionsAction">An action to configure the <see cref="CassandraConfiguration" />.</param>
    /// <param name="contextLifetime">The lifetime with which to register the context service.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddCassandraContext<TContext>(
        this IServiceCollection services,
        Action<CassandraConfiguration> optionsAction,
        ServiceLifetime contextLifetime = ServiceLifetime.Scoped) where TContext : CassandraDbContext
    {
        if (services == null) throw new ArgumentNullException(nameof(services));
        if (optionsAction == null) throw new ArgumentNullException(nameof(optionsAction));

        // Register the configuration
        var configuration = new CassandraConfiguration();
        optionsAction(configuration);
        services.AddSingleton(configuration);

        // Register the context
        var serviceDescriptor = new ServiceDescriptor(
            typeof(TContext),
            provider =>
            {
                var logger = provider.GetService<ILogger<TContext>>();
                return (TContext)Activator.CreateInstance(typeof(TContext), configuration, logger)!;
            },
            contextLifetime);

        services.Add(serviceDescriptor);

        // Also register as CassandraDbContext for generic usage
        services.Add(new ServiceDescriptor(
            typeof(CassandraDbContext),
            provider => provider.GetRequiredService<TContext>(),
            contextLifetime));

        // Register migration manager
        services.AddTransient<CassandraMigrationManager>(provider =>
        {
            var context = provider.GetRequiredService<CassandraDbContext>();
            var logger = provider.GetService<ILogger<CassandraMigrationManager>>();
            return new CassandraMigrationManager(context, logger);
        });

        return services;
    }

    /// <summary>
    /// Adds CassandraORM services with a connection string.
    /// </summary>
    /// <typeparam name="TContext">The type of context to add.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="connectionString">The Cassandra connection string.</param>
    /// <param name="contextLifetime">The lifetime with which to register the context service.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddCassandraContext<TContext>(
        this IServiceCollection services,
        string connectionString,
        ServiceLifetime contextLifetime = ServiceLifetime.Scoped) where TContext : CassandraDbContext
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string cannot be null or empty", nameof(connectionString));

        return AddCassandraContext<TContext>(services, config =>
        {
            ParseConnectionString(connectionString, config);
        }, contextLifetime);
    }

    /// <summary>
    /// Adds CassandraORM services with a configuration instance.
    /// </summary>
    /// <typeparam name="TContext">The type of context to add.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="configuration">The Cassandra configuration.</param>
    /// <param name="contextLifetime">The lifetime with which to register the context service.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddCassandraContext<TContext>(
        this IServiceCollection services,
        CassandraConfiguration configuration,
        ServiceLifetime contextLifetime = ServiceLifetime.Scoped) where TContext : CassandraDbContext
    {
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));

        return AddCassandraContext<TContext>(services, config =>
        {
            // Copy properties from the provided configuration
            config.ContactPoints = configuration.ContactPoints;
            config.Port = configuration.Port;
            config.Keyspace = configuration.Keyspace;
            config.Username = configuration.Username;
            config.Password = configuration.Password;
            config.ConsistencyLevel = configuration.ConsistencyLevel;
            config.ReplicationFactor = configuration.ReplicationFactor;
            config.UseNetworkTopologyStrategy = configuration.UseNetworkTopologyStrategy;
            config.DataCenterReplicationFactors = configuration.DataCenterReplicationFactors;
            config.AutoCreateKeyspace = configuration.AutoCreateKeyspace;
            config.ConnectionTimeout = configuration.ConnectionTimeout;
            config.QueryTimeout = configuration.QueryTimeout;
            config.EnableTracing = configuration.EnableTracing;
            config.RetryPolicy = configuration.RetryPolicy;
            config.LoadBalancingPolicy = configuration.LoadBalancingPolicy;
        }, contextLifetime);
    }

    private static void ParseConnectionString(string connectionString, CassandraConfiguration config)
    {
        // Simple connection string parser
        // Format: "ContactPoints=host1,host2;Port=9042;Keyspace=mykeyspace;Username=user;Password=pass"
        
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var part in parts)
        {
            var keyValue = part.Split('=', 2);
            if (keyValue.Length != 2) continue;

            var key = keyValue[0].Trim();
            var value = keyValue[1].Trim();

            switch (key.ToLowerInvariant())
            {
                case "contactpoints":
                case "hosts":
                    config.ContactPoints = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(h => h.Trim()).ToList();
                    break;

                case "port":
                    if (int.TryParse(value, out var port))
                        config.Port = port;
                    break;

                case "keyspace":
                    config.Keyspace = value;
                    break;

                case "username":
                case "user":
                    config.Username = value;
                    break;

                case "password":
                    config.Password = value;
                    break;

                case "consistencylevel":
                    if (Enum.TryParse<ConsistencyLevel>(value, true, out var consistency))
                        config.ConsistencyLevel = consistency;
                    break;

                case "replicationfactor":
                    if (int.TryParse(value, out var replicationFactor))
                        config.ReplicationFactor = replicationFactor;
                    break;

                case "autocreatekeyspace":
                    if (bool.TryParse(value, out var autoCreate))
                        config.AutoCreateKeyspace = autoCreate;
                    break;

                case "connectiontimeout":
                    if (int.TryParse(value, out var connectionTimeout))
                        config.ConnectionTimeout = connectionTimeout;
                    break;

                case "querytimeout":
                    if (int.TryParse(value, out var queryTimeout))
                        config.QueryTimeout = queryTimeout;
                    break;

                case "enabletracing":
                    if (bool.TryParse(value, out var enableTracing))
                        config.EnableTracing = enableTracing;
                    break;
            }
        }
    }
}

/// <summary>
/// Extension methods for CassandraDbContext.
/// </summary>
public static class CassandraDbContextExtensions
{
    /// <summary>
    /// Applies all pending migrations to the database.
    /// </summary>
    /// <param name="context">The Cassandra context.</param>
    /// <param name="migrations">The migrations to apply.</param>
    public static void Migrate(this CassandraDbContext context, params CassandraMigration[] migrations)
    {
        var migrationManager = new CassandraMigrationManager(context);
        migrationManager.MigrateUp(migrations);
    }

    /// <summary>
    /// Asynchronously applies all pending migrations to the database.
    /// </summary>
    /// <param name="context">The Cassandra context.</param>
    /// <param name="migrations">The migrations to apply.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async static Task MigrateAsync(this CassandraDbContext context, params CassandraMigration[] migrations)
    {
        var migrationManager = new CassandraMigrationManager(context);
        await migrationManager.MigrateUpAsync(migrations).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverts migrations down to the specified target version.
    /// </summary>
    /// <param name="context">The Cassandra context.</param>
    /// <param name="targetVersion">The target version to migrate down to.</param>
    /// <param name="migrations">All available migrations.</param>
    public static void MigrateDown(this CassandraDbContext context, long targetVersion, params CassandraMigration[] migrations)
    {
        var migrationManager = new CassandraMigrationManager(context);
        migrationManager.MigrateDown(targetVersion, migrations);
    }

    /// <summary>
    /// Asynchronously reverts migrations down to the specified target version.
    /// </summary>
    /// <param name="context">The Cassandra context.</param>
    /// <param name="targetVersion">The target version to migrate down to.</param>
    /// <param name="migrations">All available migrations.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async static Task MigrateDownAsync(this CassandraDbContext context, long targetVersion, params CassandraMigration[] migrations)
    {
        var migrationManager = new CassandraMigrationManager(context);
        await migrationManager.MigrateDownAsync(targetVersion, migrations).ConfigureAwait(false);
    }
}