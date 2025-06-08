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

public class ServerRepository(
    IDbContextFactory<ApplicationDbContext> context, ILogger<IServerRepository> logger) : IServerRepository
{
    public async ValueTask<Server> CreateAsync(Guid serverId, ServerInput data, Guid initiator)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync();
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

                var e = await ctx.Servers.AddAsync(server);

                Debug.Assert(await ctx.SaveChangesAsync() == 1);

                var sm = new ServerMember
                {
                    Id = Guid.NewGuid(),
                    ServerId  = serverId,
                    UserId    = initiator,
                    CreatorId = initiator,
                };

                await ctx.UsersToServerRelations.AddAsync(sm);

                Debug.Assert(await ctx.SaveChangesAsync() == 1);

                await CloneArchetypesAsync(ctx, serverId, sm.Id, initiator);

                await transaction.CommitAsync();

                return e.Entity;
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed apply trx");
                await transaction.RollbackAsync();
                throw;
            }
        });
    }


    private async ValueTask CloneArchetypesAsync(ApplicationDbContext ctx, Guid serverId, Guid serverMemberId, Guid userId)
    {
        var everyone = await ctx.Archetypes.AsNoTracking().FirstAsync(x => x.Id == Archetype.DefaultArchetype_Everyone);
        var owner    = await ctx.Archetypes.AsNoTracking().FirstAsync(x => x.Id == Archetype.DefaultArchetype_Owner);

        owner!.Id               = Guid.NewGuid();
        owner.CreatorId         = userId;
        owner.Server            = null!;
        owner.ServerId          = serverId;
        owner.ServerMemberRoles = new List<ServerMemberArchetype>();

        everyone!.Id               = Guid.NewGuid();
        everyone.CreatorId         = userId;
        everyone.ServerId          = serverId;
        everyone.Server            = null!;
        everyone.ServerMemberRoles = new List<ServerMemberArchetype>();

        await ctx.Archetypes.AddAsync(everyone);
        await ctx.Archetypes.AddAsync(owner);

        Debug.Assert(await ctx.SaveChangesAsync() == 2);

        var e = new ServerMemberArchetype()
        {
            ArchetypeId    = owner.Id,
            ServerMemberId = serverMemberId
        };

        await ctx.ServerMemberArchetypes.AddAsync(e);

        Debug.Assert(await ctx.SaveChangesAsync() == 1);
    }
}