namespace Argon.Grains;

using Microsoft.EntityFrameworkCore;

// Follow links die with the channels and spaces at either end of them.
public partial class SpaceGrain
{
    private static async Task ForgetFollowsAsync(ApplicationDbContext ctx, List<Guid> channels)
    {
        if (channels.Count == 0)
            return;

        await ctx.ChannelFollows
           .Where(f => channels.Contains(f.SourceChannelId) || channels.Contains(f.TargetChannelId))
           .ExecuteDeleteAsync();
    }

    private static async Task ForgetSpaceFollowsAsync(ApplicationDbContext ctx, Guid spaceId)
        => await ctx.ChannelFollows
           .Where(f => f.SourceSpaceId == spaceId || f.TargetSpaceId == spaceId)
           .ExecuteDeleteAsync();
}
