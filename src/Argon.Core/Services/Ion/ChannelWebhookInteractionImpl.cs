namespace Argon.Services.Ion;

using Argon.Core.Features.WebHooks;
using ion.runtime;
using Microsoft.Extensions.Configuration;

public class ChannelWebhookInteractionImpl(IConfiguration configuration) : IChannelWebhookInteraction
{
    public async Task<ICreateWebhookResult> CreateWebhook(Guid spaceId, Guid channelId, string name, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Critical);
        return Issued(await this.GetGrain<IChannelWebhooksGrain>(channelId).CreateWebhook(name, ct));
    }

    public async Task<IonArray<ChannelWebhook>> GetWebhooks(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => new(await this.GetGrain<IChannelWebhooksGrain>(channelId).GetWebhooks(ct));

    public async Task<IUpdateWebhookResult> RenameWebhook(Guid spaceId, Guid channelId, Guid webhookId, string name, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Critical);
        return await this.GetGrain<IChannelWebhooksGrain>(channelId).RenameWebhook(webhookId, name, ct);
    }

    public async Task<ICreateWebhookResult> RegenerateWebhookToken(Guid spaceId, Guid channelId, Guid webhookId, CancellationToken ct = default)
    {
        this.EnforceLockdown(LockdownSeverity.Critical);
        return Issued(await this.GetGrain<IChannelWebhooksGrain>(channelId).RegenerateWebhookToken(webhookId, ct));
    }

    public async Task<bool> DeleteWebhook(Guid spaceId, Guid channelId, Guid webhookId, CancellationToken ct = default)
        => await this.GetGrain<IChannelWebhooksGrain>(channelId).DeleteWebhook(webhookId, ct);

    // The same public base the server uses for every URL it hands to API consumers.
    private ICreateWebhookResult Issued(IssuedWebhookResult result)
        => result is { Webhook: { } webhook, Token: { } token }
            ? new SuccessCreateWebhook(webhook, token,
                IncomingWebhookEndpoint.UrlFor(configuration["Storage:Cdn:PublicBaseUrl"], webhook.webhookId, token))
            : new FailedCreateWebhook(result.Error);
}
