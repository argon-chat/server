namespace Argon.Cassandra.Migrations;

using Core;

/// <summary>
/// Manages database migrations for Cassandra.
/// </summary>
public class CassandraMigrationManager
{
    private readonly CassandraDbContext _context;
    private readonly ILogger?           _logger;

    /// <summary>
    /// Initializes a new instance of the CassandraMigrationManager class.
    /// </summary>
    /// <param name="context">The context to manage migrations for.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public CassandraMigrationManager(CassandraDbContext context, ILogger? logger = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger  = logger;
    }

    /// <summary>
    /// Ensures that the migrations table exists.
    /// </summary>
    public void EnsureMigrationsTableExists()
    {        var cql = @"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version bigint PRIMARY KEY,
                description text,
                applied_at timestamp
            )";

        _context.Session.Execute(new SimpleStatement(cql));
        _logger?.LogInformation("Ensured schema_migrations table exists");
    }

    /// <summary>
    /// Asynchronously ensures that the migrations table exists.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task EnsureMigrationsTableExistsAsync()
    {
        var cql = @"
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version bigint PRIMARY KEY,
                description text,
                applied_at timestamp
            )";

        await _context.Session.ExecuteAsync(new SimpleStatement(cql)).ConfigureAwait(false);
        _logger?.LogInformation("Ensured schema_migrations table exists");
    }

    /// <summary>
    /// Gets all applied migration versions.
    /// </summary>
    /// <returns>A set of applied migration versions.</returns>
    public HashSet<long> GetAppliedMigrations()
    {
        EnsureMigrationsTableExists();

        var result = _context.Session.Execute(new SimpleStatement("SELECT version FROM schema_migrations"));
        return new HashSet<long>(result.Select(row => row.GetValue<long>("version")));
    }

    /// <summary>
    /// Asynchronously gets all applied migration versions.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains a set of applied migration versions.</returns>
    public async Task<HashSet<long>> GetAppliedMigrationsAsync()
    {
        await EnsureMigrationsTableExistsAsync().ConfigureAwait(false);

        var result = await _context.Session.ExecuteAsync(new SimpleStatement("SELECT version FROM schema_migrations")).ConfigureAwait(false);
        return new HashSet<long>(result.Select(row => row.GetValue<long>("version")));
    }

    /// <summary>
    /// Applies all pending migrations.
    /// </summary>
    /// <param name="migrations">The migrations to apply.</param>
    public void MigrateUp(params CassandraMigration[] migrations)
    {
        var appliedMigrations = GetAppliedMigrations();
        var pendingMigrations = migrations
           .Where(m => !appliedMigrations.Contains(m.Version))
           .OrderBy(m => m.Version)
           .ToList();

        _logger?.LogInformation("Found {PendingCount} pending migrations", pendingMigrations.Count);

        foreach (var migration in pendingMigrations)
        {
            _logger?.LogInformation("Applying migration {Version}: {Description}", migration.Version, migration.Description);

            try
            {
                // Apply the migration
                migration.Up(_context.Session);

                // Record the migration
                RecordMigration(migration);

                _logger?.LogInformation("Successfully applied migration {Version}", migration.Version);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to apply migration {Version}: {Description}", migration.Version, migration.Description);
                throw;
            }
        }
    }

    /// <summary>
    /// Asynchronously applies all pending migrations.
    /// </summary>
    /// <param name="migrations">The migrations to apply.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task MigrateUpAsync(params CassandraMigration[] migrations)
    {
        var appliedMigrations = await GetAppliedMigrationsAsync().ConfigureAwait(false);
        var pendingMigrations = migrations
           .Where(m => !appliedMigrations.Contains(m.Version))
           .OrderBy(m => m.Version)
           .ToList();

        _logger?.LogInformation("Found {PendingCount} pending migrations", pendingMigrations.Count);

        foreach (var migration in pendingMigrations)
        {
            _logger?.LogInformation("Applying migration {Version}: {Description}", migration.Version, migration.Description);

            try
            {
                // Apply the migration
                await migration.UpAsync(_context.Session).ConfigureAwait(false);

                // Record the migration
                await RecordMigrationAsync(migration).ConfigureAwait(false);

                _logger?.LogInformation("Successfully applied migration {Version}", migration.Version);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to apply migration {Version}: {Description}", migration.Version, migration.Description);
                throw;
            }
        }
    }

    /// <summary>
    /// Reverts migrations down to the specified target version.
    /// </summary>
    /// <param name="targetVersion">The target version to migrate down to.</param>
    /// <param name="migrations">All available migrations.</param>
    public void MigrateDown(long targetVersion, params CassandraMigration[] migrations)
    {
        var appliedMigrations = GetAppliedMigrations();
        var migrationsToRevert = migrations
           .Where(m => appliedMigrations.Contains(m.Version) && m.Version > targetVersion)
           .OrderByDescending(m => m.Version)
           .ToList();

        _logger?.LogInformation("Found {RevertCount} migrations to revert", migrationsToRevert.Count);

        foreach (var migration in migrationsToRevert)
        {
            _logger?.LogInformation("Reverting migration {Version}: {Description}", migration.Version, migration.Description);

            try
            {
                // Revert the migration
                migration.Down(_context.Session);

                // Remove the migration record
                RemoveMigrationRecord(migration.Version);

                _logger?.LogInformation("Successfully reverted migration {Version}", migration.Version);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to revert migration {Version}: {Description}", migration.Version, migration.Description);
                throw;
            }
        }
    }

    /// <summary>
    /// Asynchronously reverts migrations down to the specified target version.
    /// </summary>
    /// <param name="targetVersion">The target version to migrate down to.</param>
    /// <param name="migrations">All available migrations.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task MigrateDownAsync(long targetVersion, params CassandraMigration[] migrations)
    {
        var appliedMigrations = await GetAppliedMigrationsAsync().ConfigureAwait(false);
        var migrationsToRevert = migrations
           .Where(m => appliedMigrations.Contains(m.Version) && m.Version > targetVersion)
           .OrderByDescending(m => m.Version)
           .ToList();

        _logger?.LogInformation("Found {RevertCount} migrations to revert", migrationsToRevert.Count);

        foreach (var migration in migrationsToRevert)
        {
            _logger?.LogInformation("Reverting migration {Version}: {Description}", migration.Version, migration.Description);

            try
            {
                // Revert the migration
                await migration.DownAsync(_context.Session).ConfigureAwait(false);

                // Remove the migration record
                await RemoveMigrationRecordAsync(migration.Version).ConfigureAwait(false);

                _logger?.LogInformation("Successfully reverted migration {Version}", migration.Version);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to revert migration {Version}: {Description}", migration.Version, migration.Description);
                throw;
            }
        }
    }

    private void RecordMigration(CassandraMigration migration)
    {
        var insert = _context.Session.Prepare(
            "INSERT INTO schema_migrations (version, description, applied_at) VALUES (?, ?, ?)");
        _context.Session.Execute(insert.Bind(migration.Version, migration.Description, DateTimeOffset.UtcNow));
    }

    private async Task RecordMigrationAsync(CassandraMigration migration)
    {
        var insert = await _context.Session.PrepareAsync(
            "INSERT INTO schema_migrations (version, description, applied_at) VALUES (?, ?, ?)").ConfigureAwait(false);
        await _context.Session.ExecuteAsync(insert.Bind(migration.Version, migration.Description, DateTimeOffset.UtcNow))
           .ConfigureAwait(false);
    }

    private void RemoveMigrationRecord(long version)
    {
        var delete = _context.Session.Prepare("DELETE FROM schema_migrations WHERE version = ?");
        _context.Session.Execute(delete.Bind(version));
    }

    private async Task RemoveMigrationRecordAsync(long version)
    {
        var delete = await _context.Session.PrepareAsync("DELETE FROM schema_migrations WHERE version = ?").ConfigureAwait(false);
        await _context.Session.ExecuteAsync(delete.Bind(version)).ConfigureAwait(false);
    }
}