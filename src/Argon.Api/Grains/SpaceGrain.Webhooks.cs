namespace Argon.Grains;

using Argon.Features.EF;

public partial class SpaceGrain
{
    /// <summary>Deleted channels take their webhooks with them; the cached tokens are dropped too.</summary>
    private async Task DropWebhooksAsync(ApplicationDbContext ctx, List<Guid> channels)
    {
        if (channels.Count == 0)
            return;

        await DropWebhooksAsync(ctx.ChannelWebhooks.Where(w => channels.Contains(w.ChannelId)));
    }

    private async Task DropSpaceWebhooksAsync(ApplicationDbContext ctx)
    {
        var spaceId = this.GetPrimaryKey();
        await DropWebhooksAsync(ctx.ChannelWebhooks.Where(w => w.SpaceId == spaceId));
    }

    private async Task DropWebhooksAsync(IQueryable<ChannelWebhookEntity> webhooks)
    {
        var ids = await webhooks.Select(w => w.Id).ToListAsync();
        if (ids.Count == 0)
            return;

        await webhooks.ExecuteDeleteAsync();

        foreach (var id in ids)
            await grainFactory.GetGrain<IIncomingWebhookGrain>(id).ForgetAsync();
    }
}
