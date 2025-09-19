namespace Argon.Api.Migrations;

using Argon.Features.EF;
using Argon.Features.Env;
using Argon.Features.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal;
using System.Data;

public static class WarmUpExtension
{
    public static WebApplication WarmUp<T>(this WebApplication app, bool isMigrate = true) where T : DbContext
    {
        if (app.Environment.IsEntryPoint())
            return app;

        using var scope = app.Services.CreateScope();

        var       factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<T>>();
        using var db      = factory.CreateDbContext();
        if (isMigrate)
            Migrate(db, scope.ServiceProvider.GetRequiredService<ILogger<T>>(), scope.ServiceProvider);
        else
            db.Database.EnsureCreated();
        return app;
    }

    public async static Task<WebApplication> WarmUpCassandra(this WebApplication app)
    {
        if (app.Environment.IsEntryPoint())
            return app;

        var options = app.Services.GetRequiredService<IOptions<FeaturesOptions>>();

        if (!options.Value.UseCassandra) return app;

        using var scope = app.Services.CreateScope();

        var controller = scope.ServiceProvider.GetRequiredService<CassandraMigrationController>();

        await controller.BeginMigrations();

        return app;
    }

    public async static Task<WebApplication> WarmUpRotations(this WebApplication app)
    {
        if (app.Environment.IsEntryPoint())
            return app;

        using var scope = app.Services.CreateScope();

        var rotationManager = scope.ServiceProvider.GetRequiredService<IVaultDbCredentialsProvider>();
        await rotationManager.EnsureLoadedAsync();
        return app;
    }

    private static void Migrate<T>(T dbCtx, ILogger<T> logger, IServiceProvider provider) where T : DbContext
    {
        var migrations = dbCtx.Database.GetPendingMigrations().ToList();
        foreach (var migration in migrations)
        {
            logger.LogInformation("Applying migration: {migration}", migration);

            var beforeHandler = provider.GetKeyedService<IBeforeMigrationsHandler>(IBeforeMigrationsHandler.Key(migration.Split('_').Last()));

            beforeHandler?.BeforeMigrateAsync(dbCtx).Wait(TimeSpan.FromMinutes(5));
            dbCtx.Database.Migrate(migration);

            logger.LogInformation("Migration applied: {migration}", migration);
        }
    }
}


public interface IBeforeMigrationsHandler
{
    public static string Key(string migrationName) => $"before_migration_{migrationName}";

    Task BeforeMigrateAsync(DbContext ctx);
}

#pragma warning disable EF1001 // Internal EF Core API usage.
public class YugabyteHistoryRepository(HistoryRepositoryDependencies dependencies) : NpgsqlHistoryRepository(dependencies)
{
    public override IMigrationsDatabaseLock AcquireDatabaseLock()
        => null;
    public override Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(null as IMigrationsDatabaseLock);
}

public class NoTransactionMigrationCommandExecutor : IMigrationCommandExecutor
{
    public void ExecuteNonQuery(IEnumerable<MigrationCommand> migrationCommands, IRelationalConnection connection)
    {
        foreach (var command in migrationCommands)
            command.ExecuteNonQuery(connection);
    }

    public async Task ExecuteNonQueryAsync(IEnumerable<MigrationCommand> migrationCommands, IRelationalConnection connection, CancellationToken cancellationToken = default)
    {
        foreach (var command in migrationCommands)
            await command.ExecuteNonQueryAsync(connection, cancellationToken: cancellationToken);
    }

    public int ExecuteNonQuery(IReadOnlyList<MigrationCommand> migrationCommands, IRelationalConnection connection,
        MigrationExecutionState executionState,
        bool commitTransaction, IsolationLevel? isolationLevel = null)
        => migrationCommands.Sum(command => command.ExecuteNonQuery(connection));

    public async Task<int> ExecuteNonQueryAsync(IReadOnlyList<MigrationCommand> migrationCommands, IRelationalConnection connection,
        MigrationExecutionState executionState,
        bool commitTransaction, IsolationLevel? isolationLevel = null, CancellationToken cancellationToken = new CancellationToken())
    {
        var i = 0;
        foreach (var command in migrationCommands)
            i += await command.ExecuteNonQueryAsync(connection, cancellationToken: cancellationToken);

        return i;
    }
}
#pragma warning restore EF1001 // Internal EF Core API usage.
