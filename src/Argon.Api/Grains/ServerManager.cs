namespace Argon.Api.Grains;

using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

public class ServerManager(
    IGrainFactory grainFactory,
    ILogger<UserManager> logger,
    ApplicationDbContext context
) : Grain, IServerManager
{
    public async Task<ServerDto> CreateServer(ServerInput input, Guid creatorId)
    {
        var server = new Server
        {
            Name = input.Name,
            Description = input.Description,
            AvatarUrl = input.AvatarUrl,
            UsersToServerRelations = new List<UsersToServerRelation>
            {
                new UsersToServerRelation
                {
                    UserId = creatorId,
                    Role = ServerRole.Owner,
                }
            },
            Channels = CreateDefaultChannels(creatorId)
        };

        context.Servers.Add(server);
        await context.SaveChangesAsync();
        return server;
    }

    private List<Channel> CreateDefaultChannels(Guid CreatorId)
    {
        List<Channel> channels = new();
        channels.Add(CreateChannel(CreatorId, "General", "General text channel", ChannelType.Text));
        channels.Add(CreateChannel(CreatorId, "General", "General voice channel", ChannelType.Voice));
        channels.Add(CreateChannel(CreatorId, "General", "General anouncements channel", ChannelType.Announcement));
        return channels;
    }

    private Channel CreateChannel(Guid CreatorId, string name, string description, ChannelType channelType)
    {
        return new()
        {
            Name = name,
            Description = description,
            UserId = CreatorId,
            ChannelType = channelType,
            AccessLevel = ServerRole.User,
        };
    }

    public async Task<ServerDto> GetServer()
    {
        return await context.Servers.FirstAsync(s => s.Id == this.GetPrimaryKey());
    }

    public async Task<ServerDto> UpdateServer(ServerInput input)
    {
        var server = context.Servers.First(s => s.Id == this.GetPrimaryKey());
        server.Name = input.Name;
        server.Description = input.Description;
        server.AvatarUrl = input.AvatarUrl;
        await context.SaveChangesAsync();
        return server;
    }

    public async Task DeleteServer()
    {
        var server = await context.Servers.FirstAsync(s => s.Id == this.GetPrimaryKey());
        context.Servers.Remove(server);
    }
}