namespace Argon.Api.Features.Utils;

public static class DbContextExtensions
{
    public async static Task<T> Select<T>(this IDbContextFactory<ApplicationDbContext> context, Func<ApplicationDbContext, Task<T>> selector)
    {
        await using var ctx = await context.CreateDbContextAsync();
        return await selector(ctx);
    }

    public async static Task<T> Select<T>(this IDbContextFactory<ApplicationDbContext> context, Func<ApplicationDbContext, Task<T>> selector, CancellationToken ct)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);
        return await selector(ctx);
    }
}