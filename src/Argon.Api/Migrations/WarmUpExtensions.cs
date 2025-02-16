namespace Argon.Api.Migrations;

using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal;

public static class WarpUpExtension
{
    public static WebApplication WarpUp<T>(this WebApplication app, bool isMigrate = true) where T : DbContext
    {
        using var scope = app.Services.CreateScope();

        var       factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<T>>();
        using var db      = factory.CreateDbContext();
        if (isMigrate)
            db.Database.Migrate();
        else
            db.Database.EnsureCreated();
        return app;
    }
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
