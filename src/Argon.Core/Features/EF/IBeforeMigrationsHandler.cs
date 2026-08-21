namespace Argon.Core.Features.EF;

public interface IBeforeMigrationsHandler
{
    public static string Key(string migrationName) => $"before_migration_{migrationName}";

    Task BeforeMigrateAsync(DbContext ctx);
}