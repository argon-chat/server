using Argon.Cassandra.Core;

namespace Argon.Cassandra.Migrations;

/// <summary>
/// Base class for Cassandra migrations.
/// </summary>
public abstract class CassandraMigration
{
    /// <summary>
    /// Gets the version of the migration.
    /// </summary>
    public abstract long Version { get; }

    /// <summary>
    /// Gets the description of the migration.
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    /// Applies the migration to the database.
    /// </summary>
    /// <param name="session">The Cassandra session to use.</param>
    public abstract void Up(ISession session);

    /// <summary>
    /// Reverts the migration from the database.
    /// </summary>
    /// <param name="session">The Cassandra session to use.</param>
    public abstract void Down(ISession session);

    /// <summary>
    /// Asynchronously applies the migration to the database.
    /// </summary>
    /// <param name="session">The Cassandra session to use.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public virtual Task UpAsync(ISession session)
    {
        Up(session);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asynchronously reverts the migration from the database.
    /// </summary>
    /// <param name="session">The Cassandra session to use.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public virtual Task DownAsync(ISession session)
    {
        Down(session);
        return Task.CompletedTask;
    }    /// <summary>
    /// Executes a CQL statement during migration.
    /// </summary>
    /// <param name="session">The Cassandra session.</param>
    /// <param name="cql">The CQL statement to execute.</param>
    protected void Execute(ISession session, string cql)
        => session.Execute(new SimpleStatement(cql));

    /// <summary>
    /// Asynchronously executes a CQL statement during migration.
    /// </summary>
    /// <param name="session">The Cassandra session.</param>
    /// <param name="cql">The CQL statement to execute.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    protected async Task ExecuteAsync(ISession session, string cql)
        => await session.ExecuteAsync(new SimpleStatement(cql)).ConfigureAwait(false);

    /// <summary>
    /// Creates a table with the specified definition.
    /// </summary>
    /// <param name="session">The Cassandra session.</param>
    /// <param name="tableName">The name of the table.</param>
    /// <param name="columns">The column definitions.</param>
    /// <param name="primaryKey">The primary key definition.</param>
    /// <param name="additionalOptions">Additional table options.</param>
    protected void CreateTable(ISession session, string tableName, string columns, string primaryKey, string? additionalOptions = null)
    {
        var cql = $@"
            CREATE TABLE IF NOT EXISTS {tableName} (
                {columns},
                PRIMARY KEY ({primaryKey})
            ){additionalOptions}";

        Execute(session, cql);
    }

    /// <summary>
    /// Drops a table.
    /// </summary>
    /// <param name="session">The Cassandra session.</param>
    /// <param name="tableName">The name of the table to drop.</param>
    protected void DropTable(ISession session, string tableName)
        => Execute(session, $"DROP TABLE IF EXISTS {tableName}");

    /// <summary>
    /// Adds a column to an existing table.
    /// </summary>
    /// <param name="session">The Cassandra session.</param>
    /// <param name="tableName">The name of the table.</param>
    /// <param name="columnName">The name of the new column.</param>
    /// <param name="columnType">The type of the new column.</param>
    protected void AddColumn(ISession session, string tableName, string columnName, string columnType)
        => Execute(session, $"ALTER TABLE {tableName} ADD {columnName} {columnType}");

    /// <summary>
    /// Drops a column from an existing table.
    /// </summary>
    /// <param name="session">The Cassandra session.</param>
    /// <param name="tableName">The name of the table.</param>
    /// <param name="columnName">The name of the column to drop.</param>
    protected void DropColumn(ISession session, string tableName, string columnName)
        => Execute(session, $"ALTER TABLE {tableName} DROP {columnName}");

    /// <summary>
    /// Creates an index on a table column.
    /// </summary>
    /// <param name="session">The Cassandra session.</param>
    /// <param name="indexName">The name of the index.</param>
    /// <param name="tableName">The name of the table.</param>
    /// <param name="columnName">The name of the column to index.</param>
    protected void CreateIndex(ISession session, string indexName, string tableName, string columnName)
        => Execute(session, $"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName} ({columnName})");

    /// <summary>
    /// Drops an index.
    /// </summary>
    /// <param name="session">The Cassandra session.</param>
    /// <param name="indexName">The name of the index to drop.</param>
    protected void DropIndex(ISession session, string indexName)
        => Execute(session, $"DROP INDEX IF EXISTS {indexName}");

    /// <summary>
    /// Creates a user-defined type.
    /// </summary>
    /// <param name="session">The Cassandra session.</param>
    /// <param name="typeName">The name of the type.</param>
    /// <param name="fields">The field definitions.</param>
    protected void CreateType(ISession session, string typeName, string fields)
        => Execute(session, $"CREATE TYPE IF NOT EXISTS {typeName} ({fields})");

    /// <summary>
    /// Drops a user-defined type.
    /// </summary>
    /// <param name="session">The Cassandra session.</param>
    /// <param name="typeName">The name of the type to drop.</param>
    protected void DropType(ISession session, string typeName)
        => Execute(session, $"DROP TYPE IF EXISTS {typeName}");
}

/// <summary>
/// Attribute to mark a class as a migration with automatic version detection.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class MigrationAttribute : Attribute
{
    /// <summary>
    /// Gets the migration version.
    /// </summary>
    public long Version { get; }

    /// <summary>
    /// Gets the migration description.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Initializes a new instance of the MigrationAttribute class.
    /// </summary>
    /// <param name="version">The migration version (typically a timestamp like 20231201120000).</param>
    /// <param name="description">The migration description.</param>
    public MigrationAttribute(long version, string description)
    {
        Version = version;
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }
}
