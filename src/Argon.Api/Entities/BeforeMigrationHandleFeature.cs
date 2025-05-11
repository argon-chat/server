namespace Argon.Entities;

using Api.Migrations;
using customMigrations;
using Microsoft.Extensions.DependencyInjection;

public static class BeforeMigrationHandleFeature
{
    public static void AddBeforeMigrations(this WebApplicationBuilder builder)
    {
        builder.Services.AddKeyedSingleton<IBeforeMigrationsHandler, EnsureAllUserHasProfile>(
            IBeforeMigrationsHandler.Key(nameof(EnsureAllUserHasProfile)));
        builder.Services.AddKeyedSingleton<IBeforeMigrationsHandler, NormalizationUsernamesMigration>(
            IBeforeMigrationsHandler.Key(nameof(NormalizationUsernamesMigration)));
    }
}