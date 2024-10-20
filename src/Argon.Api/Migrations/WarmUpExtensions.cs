namespace Argon.Api.Migrations;

using Microsoft.EntityFrameworkCore;

public static class WarpUpExtension
{
    public static WebApplication WarpUp<T>(this WebApplication app, bool isMigrate = true) where T : DbContext
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<T>();
        if (isMigrate)
            db.Database.Migrate();
        else
            db.Database.EnsureCreated();
        return app;
    }
}
