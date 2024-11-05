namespace Argon.Api.Grains;

using Entities;
using Interfaces;
using Microsoft.EntityFrameworkCore;

public class ServerManager(
    IGrainFactory        grainFactory,
    ApplicationDbContext context
) : Grain, IServerManager
{
    public async Task<ServerDto> CreateServer(ServerInput input, Guid creatorId)
    {
        var user = await grainFactory.GetGrain<IUserManager>(primaryKey: creatorId).GetUser();
        var server = new Server
        {
            Name        = input.Name,
            Description = input.Description,
            AvatarUrl   = input.AvatarUrl,
            UsersToServerRelations = new List<UsersToServerRelation>
            {
                new()
                {
                    UserId          = creatorId,
                    CustomUsername  = user.Username ?? user.Email,
                    AvatarUrl       = user.AvatarUrl,
                    CustomAvatarUrl = user.AvatarUrl,
                    Role            = ServerRole.Owner
                }
            },
            Channels = CreateDefaultChannels(CreatorId: creatorId)
        };

        context.Servers.Add(entity: server);
        await context.SaveChangesAsync();
        return await grainFactory.GetGrain<IServerManager>(primaryKey: server.Id).GetServer();
    }

    public async Task<ServerDto> GetServer()
        => await Get();

    public async Task<ServerDto> UpdateServer(ServerInput input)
    {
        var server = await Get();
        server.Name        = input.Name;
        server.Description = input.Description;
        server.AvatarUrl   = input.AvatarUrl;
        context.Servers.Update(entity: server);
        await context.SaveChangesAsync();
        return await Get();
    }

    public async Task DeleteServer()
    {
        var server = await context.Servers.FirstAsync(predicate: s => s.Id == this.GetPrimaryKey());
        context.Servers.Remove(entity: server);
        await context.SaveChangesAsync();
    }

    private List<Channel> CreateDefaultChannels(Guid CreatorId)
        =>
        [
            CreateChannel(CreatorId: CreatorId, name: "General", description: "General text channel",         channelType: ChannelType.Text),
            CreateChannel(CreatorId: CreatorId, name: "General", description: "General voice channel",        channelType: ChannelType.Voice),
            CreateChannel(CreatorId: CreatorId, name: "General", description: "General anouncements channel", channelType: ChannelType.Announcement)
        ];

    private Channel CreateChannel(Guid CreatorId, string name, string description, ChannelType channelType)
        => new()
        {
            Name        = name,
            Description = description,
            UserId      = CreatorId,
            ChannelType = channelType,
            AccessLevel = ServerRole.User
        };

    private async Task<Server> Get()
    {
        return await context.Servers
                            .Include(navigationPropertyPath: x => x.Channels)
                            .Include(navigationPropertyPath: x => x.UsersToServerRelations)
                            .FirstAsync(predicate: s => s.Id == this.GetPrimaryKey());
    }
}