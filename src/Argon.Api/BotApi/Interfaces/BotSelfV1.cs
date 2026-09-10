namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("IBotSelf", 1)]
[BotDescription("Get information about the authenticated bot and its spaces.")]
public sealed class BotSelfV1(IGrainFactory grains) : IBotInterface
{
    public sealed record BotSelfResponse(
        Guid   BotId,
        Guid   UserId,
        string Username,
        string DisplayName);

    public sealed record BotSpaceBase(
        Guid    SpaceId,
        string  Name,
        string? Description);

    public sealed record BotSpacesResponse(
        List<BotSpaceBase> Spaces);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IBotSelf");

        group.Get<BotSelfResponse>("/GetMe")
           .Summary("Returns the bot's own profile: user ID, username, display name, avatar, and email.")
           .Handle(async ctx =>
            {
                var user = await grains.GetGrain<IUserGrain>(ctx.GetBotAsUserId()).GetMe();

                return new BotSelfResponse(
                    ctx.GetBotAppId(),
                    user.Id,
                    user.Username,
                    user.DisplayName);
            });

        group.Get<BotSpacesResponse>("/GetSpaces")
           .Summary("Lists all spaces the bot has been added to.")
           .Handle(async ctx =>
            {
                var spaces = await grains.GetGrain<IUserGrain>(ctx.GetBotAsUserId()).GetMyServers();

                return new BotSpacesResponse(
                    spaces.Select(s => new BotSpaceBase(s.spaceId, s.name, s.description)).ToList());
            });
    }
}
