namespace Argon.Entities.customMigrations;

using Api.Migrations;

public class EnsureAllUserHasProfile(ILogger<EnsureAllUserHasProfile> logger) : IBeforeMigrationsHandler
{
    public async Task BeforeMigrateAsync(DbContext context)
    {
        if (context is not ApplicationDbContext ctx)
            throw new ArgumentException("incorrect type for db context");


        const int batchSize = 500;

        var userIdsWithoutProfile = await ctx.Users
           .Where(u => u.Profile == null!)
           .Select(u => u.Id)
           .ToListAsync();
        var total = 0;
        foreach (var batch in userIdsWithoutProfile.Chunk(batchSize))
        {
            var profiles = batch.Select(userId => new UserProfile
            {
                UserId    = userId,
                Id        = Guid.NewGuid(),
                IsPremium = false,
                Badges    = []
            });

            await ctx.UserProfiles.AddRangeAsync(profiles);
            total += await ctx.SaveChangesAsync();
        }

        logger.LogWarning("Before migration applied! Ensured profiles: {total}", total);
    }
}