namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;

[BotInterface("IChannels", 1)]
[BotDescription("Create, list, and delete channels within a space.")]
[StableContract("0fe77bb6a54be4f1494413f214690ba904602c7a5b4637bdbc3a4a12237fc1b2")]
[BotRoute("GET",    "/List",   ResponseType = typeof(ChannelListResponse), Description = "Lists all channels in a space. Pass spaceId as a query parameter.")]
[BotRoute("POST",   "/Create", RequestType = typeof(CreateChannelRequest), ResponseType = typeof(BotChannel), Description = "Creates a new channel in a space. Specify name, type (text or voice), and optionally a channel group.", Permission = "ManageChannels")]
[BotRoute("DELETE", "/Delete", ResponseType = typeof(DeletedResponse), Description = "Deletes a channel. Pass spaceId and channelId as query parameters.", Permission = "ManageChannels")]
[BotError("/List", 403, "not_a_member", "Bot is not a member of this space.")]
[BotError("/Create", 403, "not_a_member", "Bot is not a member of this space.")]
[BotError("/Delete", 403, "not_a_member", "Bot is not a member of this space.")]
public sealed class ChannelsV1(IGrainFactory grains) : IBotInterface
{
    public sealed record CreateChannelRequest(
        Guid        SpaceId,
        string      Name,
        string?     Description,
        ChannelType ChannelType,
        Guid?       GroupId = null);

    public sealed record BotChannel(
        Guid        ChannelId,
        Guid        SpaceId,
        string      Name,
        string?     Description,
        string      ChannelType,
        Guid?       GroupId);

    public sealed record ChannelListResponse(
        List<BotChannel> Channels);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.AddEndpointFilter<BotSpaceMembershipFilter>();

        group.MapGet("/List", async (Guid spaceId) =>
        {
            var space    = grains.GetGrain<ISpaceGrain>(spaceId);
            var channels = await space.GetChannels();
            return Results.Ok(new ChannelListResponse(
                channels.Select(c => new BotChannel(
                    c.channel.channelId,
                    c.channel.spaceId,
                    c.channel.name,
                    c.channel.description,
                    c.channel.type.ToString(),
                    c.channel.groupId)).ToList()));
        });

        group.MapPost("/Create", async (CreateChannelRequest request) =>
        {
            try
            {
                var space   = grains.GetGrain<ISpaceGrain>(request.SpaceId);
                var channel = await space.CreateChannel(
                    new ChannelInput(request.Name, request.Description, request.ChannelType),
                    request.GroupId);
                return Results.Ok(new BotChannel(
                    channel.Id,
                    channel.SpaceId,
                    channel.Name,
                    channel.Description,
                    channel.ChannelType.ToString(),
                    channel.ChannelGroupId));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(
                    new BotApiError("not_a_member", "Bot does not have ManageChannels permission."),
                    statusCode: StatusCodes.Status403Forbidden);
            }
        });

        group.MapDelete("/Delete", async (Guid spaceId, Guid channelId) =>
        {
            try
            {
                var space = grains.GetGrain<ISpaceGrain>(spaceId);
                await space.DeleteChannel(channelId);
                return Results.Ok(new DeletedResponse(true));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(
                    new BotApiError("not_a_member", "Bot does not have ManageChannels permission."),
                    statusCode: StatusCodes.Status403Forbidden);
            }
        });
    }
}
