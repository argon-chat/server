namespace Argon.Core.Features.EF;

using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations.Internal;

#pragma warning disable EF1001

public class NoLockHistoryRepository(HistoryRepositoryDependencies dependencies) : NpgsqlHistoryRepository(dependencies)
{
    public override IMigrationsDatabaseLock AcquireDatabaseLock()
        => new NoopMigrationsDatabaseLock(this);

    public override Task<IMigrationsDatabaseLock> AcquireDatabaseLockAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IMigrationsDatabaseLock>(new NoopMigrationsDatabaseLock(this));

    private class NoopMigrationsDatabaseLock(NpgsqlHistoryRepository historyRepository) : IMigrationsDatabaseLock
    {
        public void Dispose()
        {
        }

        public ValueTask          DisposeAsync()    => ValueTask.CompletedTask;
        public IHistoryRepository HistoryRepository => historyRepository;
    }
}