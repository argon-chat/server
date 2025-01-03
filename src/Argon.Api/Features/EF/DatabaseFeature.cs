namespace Argon.Features.EF;

public static class DatabaseFeature
{
    public static void AddPooledDatabase<T>(this WebApplicationBuilder builder) where T : DbContext
        => builder.Services.AddPooledDbContextFactory<T>(x => x
           .EnableDetailedErrors().EnableSensitiveDataLogging().UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
           .AddInterceptors(new TimeStampAndSoftDeleteInterceptor()));
}