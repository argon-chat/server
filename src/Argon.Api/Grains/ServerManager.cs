namespace Argon.Api.Grains;

using Entities;
using global::Grains.Interface;
using Microsoft.EntityFrameworkCore;
using Models;
using Models.DTO;

public class ServerManager(IGrainFactory grainFactory, ApplicationDbContext context) : Grain, IServerManager
{
    public async Task<ServerDto> CreateServer(ServerInput input, Guid creatorId)
    {
        var user = await grainFactory.GetGrain<IUserManager>(creatorId).GetUser();
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
            Channels = CreateDefaultChannels(creatorId)
        };

        context.Servers.Add(server);
        await context.SaveChangesAsync();
        return await grainFactory.GetGrain<IServerManager>(server.Id).GetServer();
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