namespace Argon.Grains;

using Argon.Features.EF;

public partial class SpaceGrain
{
    /// <summary>
    /// Deleted channels take their webhooks with them. Nothing is sent to the webhook grains: each post
    /// reads the row, so a deleted one answers 404 at once.
    /// </summary>
    private async Task DropWebhooksAsync(ApplicationDbContext ctx, List<Guid> channels)
    {
        if (channels.Count == 0)
            return;

        await ctx.ChannelWebhooks.Where(w => channels.Contains(w.ChannelId)).ExecuteDeleteAsync();
    }

    private async Task DropSpaceWebhooksAsync(ApplicationDbContext ctx)
    {
        var spaceId = this.GetPrimaryKey();
        await ctx.ChannelWebhooks.Where(w => w.SpaceId == spaceId).ExecuteDeleteAsync();
    }
}
