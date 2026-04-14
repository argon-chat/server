namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;

[BotInterface("IMembers", 1)]
[BotDescription("Manage space members: kick users from channels.")]
[StableContract("523eb9321577603b796012867277baad830afaf46b0c121f03864f25a9c17b85")]
[BotRoute("POST", "/Kick", ResponseType = typeof(KickResponse), Description = "Kicks a user from a channel. Pass spaceId, channelId, and userId as query parameters.", Permission = "KickMembers")]
[BotError("/Kick", 400, "kick_failed", "Failed to kick user — they may not be in the channel.")]
[BotError("/Kick", 403, "not_a_member", "Bot is not a member of this space.")]
public sealed class MembersV1(IGrainFactory grains) : IBotInterface
{
    public sealed record KickResponse(
        bool Kicked);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IMembers");

        group.MapPost("/Kick", async (Guid spaceId, Guid channelId, Guid userId) =>
        {
            var channel = grains.GetGrain<IChannelGrain>(channelId);
            var result  = await channel.KickMemberFromChannel(userId);
            return result
                ? Results.Ok(new KickResponse(true))
                : Results.BadRequest(new BotApiError("kick_failed"));
        });
    }
}
