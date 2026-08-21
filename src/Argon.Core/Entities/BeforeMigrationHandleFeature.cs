namespace Argon.Entities;

public static class BeforeMigrationHandleFeature
{
    public static void AddBeforeMigrations(this WebApplicationBuilder builder)
    {
        //builder.Services.AddKeyedSingleton<IBeforeMigrationsHandler, EnsureAllUserHasProfile>(
        //    IBeforeMigrationsHandler.Key(nameof(EnsureAllUserHasProfile)));
        //builder.Services.AddKeyedSingleton<IBeforeMigrationsHandler, NormalizationUsernamesMigration>(
        //    IBeforeMigrationsHandler.Key(nameof(NormalizationUsernamesMigration)));
    }
}