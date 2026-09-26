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

    private static readonly BotError InvalidChannel = new(400, "validation_error",
        "The channel name, description, type or group is invalid.");

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
           .Throws(InvalidChannel)
           .Handle(async (_, request) =>
            {
                var (error, channel) = await grains.GetGrain<ISpaceGrain>(request.SpaceId).CreateChannel(
                    new ChannelInput(request.Name, request.Description, request.ChannelType),
                    request.GroupId);

                if (error is not ChannelLayoutError.NONE)
                    throw error switch
                    {
                        ChannelLayoutError.NO_PERMISSION => NoManageChannels.Raise(),
                        ChannelLayoutError.NOT_FOUND     => InvalidChannel.Raise("Channel group not found"),
                        ChannelLayoutError.INVALID_DATA  => InvalidChannel.Raise(),
                        _                                => throw new InvalidOperationException($"unhandled ChannelLayoutError {error}")
                    };

                return new BotChannel(
                    channel!.Id,
                    channel.SpaceId,
                    channel.Name,
                    channel.Description,
                    channel.ChannelType.ToString(),
                    channel.ChannelGroupId);
            });

        group.Delete<DeleteChannelQuery, DeletedResponse>("/Delete")
           .Summary("Deletes a channel.")
           .Permission(ArgonEntitlement.ManageChannels)
           .RequiresSpaceMembership()
           .Throws(NoManageChannels)
           .Handle(async (_, query) =>
            {
                var error = await grains.GetGrain<ISpaceGrain>(query.SpaceId).DeleteChannel(query.ChannelId);

                if (error is ChannelLayoutError.NO_PERMISSION)
                    throw NoManageChannels.Raise();
                if (error is not ChannelLayoutError.NONE)
                    throw new InvalidOperationException($"unhandled ChannelLayoutError {error}");

                return new DeletedResponse(true);
            });
    }
}
