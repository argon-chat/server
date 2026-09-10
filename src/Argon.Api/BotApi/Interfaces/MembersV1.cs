namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("IMembers", 1)]
[BotDescription("Manage space members: kick users from channels.")]
public sealed class MembersV1(IGrainFactory grains) : IBotInterface
{
    public sealed record KickQuery(Guid SpaceId, Guid ChannelId, Guid UserId);

    public sealed record KickResponse(bool Kicked);

    private static readonly BotError KickFailed = new(400, "kick_failed",
        "Failed to kick user — they may not be in the channel.");

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IMembers");

        group.Post<KickQuery, KickResponse>("/Kick")
           .FromQuery()
           .Summary("Kicks a user from a channel.")
           .Permission(ArgonEntitlement.KickMember)
           .Throws(KickFailed)
           .Handle(async (_, query) =>
            {
                var channel = grains.GetGrain<IChannelGrain>(query.ChannelId);

                if (!await channel.KickMemberFromChannel(query.UserId))
                    throw KickFailed.Raise();

                return new KickResponse(true);
            });
    }
}
