namespace Argon.Features.Repositories;

using System.Diagnostics;

public static class TemplateFeature
{
    public static IServiceCollection AddEfRepositories(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IServerRepository, ServerRepository>();
        return builder.Services;
    }
}

public interface IServerRepository
{
    ValueTask<Server> CreateAsync(Guid serverId, ServerInput data, Guid initiator);
}

public class ServerRepository(ApplicationDbContext context) : IServerRepository
{
    public async ValueTask<Server> CreateAsync(Guid serverId, ServerInput data, Guid initiator)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            try
            {
                var server = new Server()
                {
                    Id           = serverId,
                    AvatarFileId = data.AvatarUrl,
                    CreatorId    = initiator,
                    Description  = data.Description,
                    Name         = data.Name!
                };

                var e = await context.Servers.AddAsync(server);

                await context.SaveChangesAsync();

                var sm = new ServerMember()
                {
                    Id        = initiator,
                    ServerId  = serverId,
                    UserId    = initiator,
                    CreatorId = initiator,
                };

                await context.UsersToServerRelations.AddAsync(sm);

                await context.SaveChangesAsync();

                await CloneArchetypesAsync(e.Entity.Id, sm.Id);

                await transaction.CommitAsync();

                return e.Entity;
            }
            catch (Exception)
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }


    private async ValueTask CloneArchetypesAsync(Guid serverId, Guid initiator)
    {
        var everyone = await context.Archetypes.FindAsync(Archetype.DefaultArchetype_Everyone);
        var owner    = await context.Archetypes.FindAsync(Archetype.DefaultArchetype_Owner);

        owner!.Id               = Guid.NewGuid();
        owner.CreatorId         = initiator;
        owner.Server            = null!;
        owner.ServerId          = serverId;
        owner.ServerMemberRoles = new List<ServerMemberArchetype>();

        everyone!.Id               = Guid.NewGuid();
        everyone.CreatorId         = initiator;
        everyone.ServerId          = serverId;
        everyone.Server            = null!;
        everyone.ServerMemberRoles = new List<ServerMemberArchetype>();

        await context.Archetypes.AddAsync(everyone);
        await context.Archetypes.AddAsync(owner);

        Debug.Assert(await context.SaveChangesAsync() == 2);

        var e = new ServerMemberArchetype()
        {
            ArchetypeId    = owner.Id,
            ServerMemberId = initiator
        };

        await context.ServerMemberArchetypes.AddAsync(e);

        Debug.Assert(await context.SaveChangesAsync() == 1);
    }
}