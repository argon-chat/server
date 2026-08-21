namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("IBotSelf", 1)]
[BotDescription("Get information about the authenticated bot and its spaces.")]
[StableContract("6e0e64decfe0f7b185bdb5e6b92dacbba557e5a9dd72e0c29b8c6e5094ea819b")]
[BotRoute("GET", "/GetMe",    ResponseType = typeof(BotSelfResponse), Description = "Returns the bot's own profile: user ID, username, display name, avatar, and email.")]
[BotRoute("GET", "/GetSpaces", ResponseType = typeof(BotSpacesResponse), Description = "Lists all spaces the bot has been added to.")]
public sealed class BotSelfV1(IGrainFactory grains) : IBotInterface
{
    public sealed record BotSelfResponse(
        Guid    BotId,
        Guid    UserId,
        string  Username,
        string  DisplayName);

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

        group.MapGet("/GetMe", async (HttpContext ctx) =>
        {
            var userId = ctx.GetBotAsUserId();
            var user   = await grains.GetGrain<IUserGrain>(userId).GetMe();
            return Results.Ok(new BotSelfResponse(
                ctx.GetBotAppId(),
                user.Id,
                user.Username,
                user.DisplayName));
        });

        group.MapGet("/GetSpaces", async (HttpContext ctx) =>
        {
            var userId = ctx.GetBotAsUserId();
            var spaces = await grains.GetGrain<IUserGrain>(userId).GetMyServers();
            return Results.Ok(new BotSpacesResponse(
                spaces.Select(s => new BotSpaceBase(s.spaceId, s.name, s.description)).ToList()));
        });
    }
}
