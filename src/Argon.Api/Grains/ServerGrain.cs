namespace Argon.Api.Grains;

using ActualLab.Collections;
using Contracts;
using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Argon.Api.Features.Rpc;
using AutoMapper;

public class ServerGrain(IGrainFactory grainFactory, ApplicationDbContext context, IMapper mapper) : Grain, IServerGrain
{
    private IArgonStream<IArgonEvent> _serverEvents;

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
        => _serverEvents = await this.Streams().CreateServerStream();


    public async Task<ServerDto> CreateServer(ServerInput input, Guid creatorId)
    {
        var server = new Server
        {
            Id = this.GetPrimaryKey(),
            Name        = input.Name,
            Description = input.Description,
            AvatarUrl   = input.AvatarUrl,
            UsersToServerRelations =
            [
                new()
                {
                    UserId          = creatorId,
                    Role            = ServerRole.Owner
                }
            ],
            Channels = CreateDefaultChannels(creatorId)
        };

        context.Servers.Add(server);
        await context.SaveChangesAsync();
        return await GetServer();
    }

    public async Task<ServerDto> GetServer() => mapper.Map<ServerDto>(await Get());

    public async Task<ServerDto> UpdateServer(ServerInput input)
    {
        var server = await Get();
        server.Name        = input.Name;
        server.Description = input.Description;
        server.AvatarUrl   = input.AvatarUrl;
        context.Servers.Update(server);
        await context.SaveChangesAsync();
        await _serverEvents.Fire(new ServerModified(PropertyBag.Empty
           .Set("name", input.Name)
           .Set("description", input.Description)
           .Set("avatarUrl", input.AvatarUrl)
        ));
        return mapper.Map<ServerDto>(await Get());
    }

    public async Task DeleteServer()
    {
        var server = await context.Servers.FirstAsync(s => s.Id == this.GetPrimaryKey());
        context.Servers.Remove(server);
        await context.SaveChangesAsync();
    }

    public async Task<ChannelDto> CreateChannel(ChannelInput input)
    {
        var channel = new Channel
        {
            Name        = input.Name,
            AccessLevel = input.AccessLevel
        };
        channel.Description = input.Description ?? channel.Description;
        channel.ChannelType = input.ChannelType;
        context.Channels.Update(channel);
        await context.SaveChangesAsync();
        await _serverEvents.Fire(new ChannelCreated(channel.Id));
        return mapper.Map<ChannelDto>(channel);
    }

    private List<Channel> CreateDefaultChannels(Guid CreatorId) =>
    [
        CreateChannel(CreatorId, "General", "General text channel", ChannelType.Text),
        CreateChannel(CreatorId, "General", "General voice channel", ChannelType.Voice),
        CreateChannel(CreatorId, "General", "General anouncements channel", ChannelType.Announcement)
    ];

    private Channel CreateChannel(Guid CreatorId, string name, string description, ChannelType channelType) => new()
    {
        Name        = name,
        Description = description,
        UserId      = CreatorId,
        ChannelType = channelType,
        AccessLevel = ServerRole.User
    };

    private async Task<Server> Get() => await context.Servers.Include(x => x.Channels).Include(x => x.UsersToServerRelations)
       .FirstAsync(s => s.Id == this.GetPrimaryKey());
}