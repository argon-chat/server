namespace Argon.Features.EF;

using Cassandra;

public class CassandraMigrationController(
    ICluster cluster, 
    IOptions<CassandraOptions> options, 
    ILogger<CassandraMigrationController> logger)
{

    public async Task BeginMigrations()
    {
        var conn = await EnsureKeyspaceCreated();
        await EnsureExistMigrationTable(conn);
        await ApplyMigrations(conn);
    }

    private async Task<ISession> EnsureKeyspaceCreated()
    {
        var conn = await cluster.ConnectAsync();

        var sql =
            $$"""
                CREATE KEYSPACE IF NOT EXISTS {{options.Value.KeySpace}}
                WITH replication = { 'class': 'SimpleStrategy', 'replication_factor': 1 };
            """;

        await conn.ExecuteAsync(new SimpleStatement(sql));

        return await cluster.ConnectAsync(options.Value.KeySpace);
    }

    private async Task ApplyMigrations(ISession session)
    {
        var appliedIds = await GetAppliedMigrationIds(session);

        var migrationFiles = Directory.GetFiles("Migrations/Cassandra", "*.cql")
           .OrderBy(Path.GetFileName)
           .ToList();

        foreach (var file in migrationFiles)
        {
            var id = Path.GetFileName(file);
            if (appliedIds.Contains(id))
            {
                logger.LogInformation("Skipping already applied migration: {MigrationId}", id);
                continue;
            }

            logger.LogInformation("Applying migration: {MigrationId}", id);

            var cql = await File.ReadAllTextAsync(file);

            await session.ExecuteAsync(new SimpleStatement(cql));

            await session.ExecuteAsync(new SimpleStatement(
                "INSERT INTO cassandra_migrations (id, applied_at) VALUES (?, toTimestamp(now()))",
                id
            ));

            logger.LogInformation("Applied migration: {MigrationId}", id);
        }
    }

    private async Task<HashSet<string>> GetAppliedMigrationIds(ISession session)
    {
        var result = await session.ExecuteAsync(new SimpleStatement(
            "SELECT id FROM cassandra_migrations"
        ));

        return result.Select(row => row.GetValue<string>("id")).ToHashSet();
    }

    private async static Task EnsureExistMigrationTable(ISession session)
    {
        const string sql =
        """
        CREATE TABLE IF NOT EXISTS cassandra_migrations (
           id TEXT PRIMARY KEY,
           applied_at TIMESTAMP
        )
        """;
        await session.ExecuteAsync(new SimpleStatement(sql));
    }
}