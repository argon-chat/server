namespace Argon.Api.Grains;

using Contracts;
using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using Orleans.Streams;
using Argon.Api.Features.Rpc;

public class ServerGrain(IGrainFactory grainFactory, ApplicationDbContext context) : Grain, IServerGrain
{
    private IAsyncStream<IArgonEvent> _serverEvents;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _serverEvents = this.Streams().CreateServerStream<ServerEvent>();
        return Task.CompletedTask;
    }


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
                    CustomUsername  = null,
                    AvatarUrl       = null,
                    CustomAvatarUrl = null,
                    Role            = ServerRole.Owner
                }
            ],
            Channels = CreateDefaultChannels(creatorId)
        };

        context.Servers.Add(server);
        await context.SaveChangesAsync();
        return await GetServer();
    }

    public async Task<ServerDto> GetServer() => await Get();

    public async Task<ServerDto> UpdateServer(ServerInput input)
    {
        var server = await Get();
        server.Name        = input.Name;
        server.Description = input.Description;
        server.AvatarUrl   = input.AvatarUrl;
        context.Servers.Update(server);
        await context.SaveChangesAsync();
        return await Get();
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
        return channel;
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