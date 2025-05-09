namespace Argon.Entities;

using Api.Migrations;
using customMigrations;
using Microsoft.Extensions.DependencyInjection;

public static class BeforeMigrationHandleFeature
{
    public static void AddBeforeMigrations(this WebApplicationBuilder builder)
        => builder.Services.AddKeyedSingleton<IBeforeMigrationsHandler>(IBeforeMigrationsHandler.Key(nameof(EnsureAllUserHasProfile)));
}