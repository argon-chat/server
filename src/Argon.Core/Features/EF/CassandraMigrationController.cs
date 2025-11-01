namespace Argon.Features.EF;

using Cassandra.Core;

public class CassandraMigrationController(
    ICassandraDbContextFactory<ArgonCassandraDbContext> dbCtx, 
    IOptions<CassandraOptions> options, 
    ILogger<CassandraMigrationController> logger)
{

    public async Task BeginMigrations()
    {
        if (options.Value.Disabled)
        {
            logger.LogWarning($"Cassandra adapter has disabled by config, skip migrations and ensure keyspace...");
            return;
        }

        await using var ctx = await dbCtx.CreateDbContextAsync();
        await EnsureKeyspaceCreated(ctx.Context);
        await EnsureExistMigrationTable(ctx.Context);
        await ApplyMigrations(ctx.Context);
    }

    private Task EnsureKeyspaceCreated(ArgonCassandraDbContext ctx)
    {
        ctx.Session.CreateKeyspaceIfNotExists(options.Value.KeySpace, new Dictionary<string, string>()
        {
            { "class", "SimpleStrategy" },
            { "replication_factor", "1" }
        });
        return Task.CompletedTask;
    }

    private static List<string> GetMigrationFiles(string relativePath = "Migrations/Cassandra")
    {
        var baseDir    = AppContext.BaseDirectory;
        var candidate1 = Path.Combine(baseDir, relativePath);
        var candidate2 = Path.GetFullPath(relativePath);

        var migrationDir = Directory.Exists(candidate1) ? candidate1 :
            Directory.Exists(candidate2)                ? candidate2 :
                                                          throw new DirectoryNotFoundException(
                                                              $"Migration folder not found in '{candidate1}', or in '{candidate2}'");

        return Directory.GetFiles(migrationDir, "*.cql")
           .OrderBy(Path.GetFileName)
           .ToList();
    }

    private async Task ApplyMigrations(ArgonCassandraDbContext ctx)
    {
        var appliedIds = await GetAppliedMigrationIds(ctx);

        var migrationFiles = GetMigrationFiles();

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

            await ctx.ExecuteCqlAsync(cql);

            await ctx.ExecuteCqlAsync("INSERT INTO cassandra_migrations (id, applied_at) VALUES (?, toTimestamp(now()))",
                id);

            logger.LogInformation("Applied migration: {MigrationId}", id);
        }
    }

    private async Task<HashSet<string>> GetAppliedMigrationIds(ArgonCassandraDbContext ctx)
    {
        var result = await ctx.ExecuteCqlAsync("SELECT id FROM cassandra_migrations");

        return result.Select(row => row.GetValue<string>("id")).ToHashSet();
    }

    private async Task EnsureExistMigrationTable(ArgonCassandraDbContext ctx)
    {
        const string sql =
        """
        CREATE TABLE IF NOT EXISTS cassandra_migrations (
           id TEXT PRIMARY KEY,
           applied_at TIMESTAMP
        )
        """;
        await ctx.ExecuteCqlAsync(sql);
    }
}