namespace Argon.Features.Repositories;

using System.Diagnostics;
using Shared;

public interface IServerRepository
{
    ValueTask<Server> CreateAsync(Guid serverId, ServerInput data, Guid initiator);
    ValueTask         GrantDefaultArchetypeTo(ApplicationDbContext ctx, Guid serverId, Guid serverMemberId);
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

                Ensure.That(await ctx.SaveChangesAsync() == 1);

                var sm = new ServerMember
                {
                    Id = Guid.NewGuid(),
                    ServerId  = serverId,
                    UserId    = initiator,
                    CreatorId = initiator,
                };

                await ctx.UsersToServerRelations.AddAsync(sm);

                Ensure.That(await ctx.SaveChangesAsync() == 1);

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

    public async ValueTask GrantDefaultArchetypeTo(ApplicationDbContext ctx, Guid serverId, Guid serverMemberId)
    {
        var everyone = await ctx.Archetypes
           .AsNoTracking()
           .FirstAsync(x => x.IsDefault && x.ServerId == serverId);

        var e1 = new ServerMemberArchetype
        {
            ArchetypeId    = everyone.Id,
            ServerMemberId = serverMemberId
        };

        await ctx.ServerMemberArchetypes.AddAsync(e1);

        Ensure.That(await ctx.SaveChangesAsync() == 1);
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
        everyone.IsDefault         = true;

        await ctx.Archetypes.AddAsync(everyone);
        await ctx.Archetypes.AddAsync(owner);

        Ensure.That(await ctx.SaveChangesAsync() == 2);

        var e1 = new ServerMemberArchetype()
        {
            ArchetypeId    = owner.Id,
            ServerMemberId = serverMemberId
        };

        await ctx.ServerMemberArchetypes.AddAsync(e1);

        Ensure.That(await ctx.SaveChangesAsync() == 1);

        var e2 = new ServerMemberArchetype()
        {
            ArchetypeId    = everyone.Id,
            ServerMemberId = serverMemberId
        };

        await ctx.ServerMemberArchetypes.AddAsync(e2);

        Ensure.That(await ctx.SaveChangesAsync() == 1);
    }
}