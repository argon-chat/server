namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;

[BotInterface("IChannels", 1)]
[BotDescription("Create, list, and delete channels within a space.")]
public sealed class ChannelsV1(IGrainFactory grains) : IBotInterface
{
    public sealed record SpaceQuery(Guid SpaceId);

    public sealed record DeleteChannelQuery(Guid SpaceId, Guid ChannelId);

    public sealed record CreateChannelRequest(
        Guid        SpaceId,
        string      Name,
        string?     Description,
        ChannelType ChannelType,
        Guid?       GroupId = null);

    public sealed record BotChannel(
        Guid    ChannelId,
        Guid    SpaceId,
        string  Name,
        string? Description,
        string  ChannelType,
        Guid?   GroupId);

    public sealed record ChannelListResponse(
        List<BotChannel> Channels);

    // The code is "not_a_member" although this is a missing entitlement rather than missing
    // membership: bots switch on it today, so correcting it waits for an IChannels/v2.
    private static readonly BotError NoManageChannels = new(403, "not_a_member",
        "Bot does not have ManageChannels permission.");

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IChannels");

        group.Get<SpaceQuery, ChannelListResponse>("/List")
           .Summary("Lists all channels in a space.")
           .RequiresSpaceMembership()
           .Handle(async (_, query) =>
            {
                var channels = await grains.GetGrain<ISpaceReadGrain>(query.SpaceId).GetChannels();

                return new ChannelListResponse(channels.Select(c => new BotChannel(
                    c.channel.channelId,
                    c.channel.spaceId,
                    c.channel.name,
                    c.channel.description,
                    c.channel.type.ToString(),
                    c.channel.groupId)).ToList());
            });

        group.Post<CreateChannelRequest, BotChannel>("/Create")
           .Summary("Creates a new channel in a space. Specify name, type (text or voice), and optionally a channel group.")
           .Permission(ArgonEntitlement.ManageChannels)
           .RequiresSpaceMembership()
           .Throws(NoManageChannels)
           .Handle(async (_, request) =>
            {
                try
                {
                    var channel = await grains.GetGrain<ISpaceGrain>(request.SpaceId).CreateChannel(
                        new ChannelInput(request.Name, request.Description, request.ChannelType),
                        request.GroupId);

                    return new BotChannel(
                        channel.Id,
                        channel.SpaceId,
                        channel.Name,
                        channel.Description,
                        channel.ChannelType.ToString(),
                        channel.ChannelGroupId);
                }
                catch (UnauthorizedAccessException)
                {
                    throw NoManageChannels.Raise();
                }
            });

        group.Delete<DeleteChannelQuery, DeletedResponse>("/Delete")
           .Summary("Deletes a channel.")
           .Permission(ArgonEntitlement.ManageChannels)
           .RequiresSpaceMembership()
           .Throws(NoManageChannels)
           .Handle(async (_, query) =>
            {
                try
                {
                    await grains.GetGrain<ISpaceGrain>(query.SpaceId).DeleteChannel(query.ChannelId);
                    return new DeletedResponse(true);
                }
                catch (UnauthorizedAccessException)
                {
                    throw NoManageChannels.Raise();
                }
            });
    }
}
