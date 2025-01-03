namespace Argon.Api.Migrations;

using Microsoft.EntityFrameworkCore;

public static class WarpUpExtension
{
    public static WebApplication WarpUp<T>(this WebApplication app, bool isMigrate = true) where T : DbContext
    {
        using var scope = app.Services.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<T>>();
        using var db      = factory.CreateDbContext();

        if (isMigrate)
            db.Database.Migrate();
        else
            db.Database.EnsureCreated();
        return app;
    }
}