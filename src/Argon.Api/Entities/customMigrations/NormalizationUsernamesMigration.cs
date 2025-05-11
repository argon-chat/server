namespace Argon.Entities.customMigrations;

using Argon.Api.Migrations;

public class NormalizationUsernamesMigration(ILogger<NormalizationUsernamesMigration> logger) : IBeforeMigrationsHandler
{
    public async Task BeforeMigrateAsync(DbContext context)
    {
        if (context is not ApplicationDbContext ctx)
            throw new ArgumentException("incorrect type for db context");


        const int batchSize = 500;

        var userIdsWithoutProfile = await ctx.Users
           .Where(u => u.NormalizedUsername == null!)
           .ToListAsync();
        var total = 0;
        foreach (var batch in userIdsWithoutProfile.Chunk(batchSize))
        {
            foreach (var user in batch)
                user.NormalizedUsername = user.Username.ToLowerInvariant();

            ctx.Users.UpdateRange(batch);
            total += await ctx.SaveChangesAsync();
        }

        logger.LogWarning("Before migration applied! Ensured normalized usernames: {total}", total);
    }
}