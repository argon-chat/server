namespace Argon.Features.Repositories;

using Shared;
using System.Data;

public interface IServerRepository
{
    /// <returns>The new space, or <c>null</c> when the initiator already owns <see cref="ServerRepository.MaxOwnedSpacesPerUser"/> spaces.</returns>
    ValueTask<SpaceEntity?> CreateAsync(Guid spaceId, ServerInput data, Guid initiator);

    /// <summary>Stages the grant; the caller's SaveChanges commits it together with the membership.</summary>
    ValueTask GrantDefaultArchetypeTo(ApplicationDbContext ctx, Guid spaceId, Guid serverMemberId);
}

public class ServerRepository(IDbContextFactory<ApplicationDbContext> context) : IServerRepository
{
    public const int MaxOwnedSpacesPerUser = 10;

    public async ValueTask<SpaceEntity?> CreateAsync(Guid spaceId, ServerInput data, Guid initiator)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var strategy = ctx.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            ctx.ChangeTracker.Clear();

            // Serializable so that two concurrent creations by one user cannot both pass the count.
            await using var transaction = await ctx.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            var owned = await ctx.Spaces.CountAsync(s => s.CreatorId == initiator);
            if (owned >= MaxOwnedSpacesPerUser)
                return null;

            var server = new SpaceEntity()
            {
                Id           = spaceId,
                AvatarFileId = data.AvatarUrl,
                CreatorId    = initiator,
                Description  = data.Description,
                Name         = data.Name!
            };

            ctx.Spaces.Add(server);

            var sm = new SpaceMemberEntity
            {
                Id        = ArgonId.New(),
                SpaceId   = spaceId,
                UserId    = initiator,
                CreatorId = initiator,
            };

            ctx.UsersToServerRelations.Add(sm);

            await CloneArchetypesAsync(ctx, spaceId, sm.Id, initiator);

            await ctx.SaveChangesAsync();
            await transaction.CommitAsync();

            return server;
        });
    }

    public async ValueTask GrantDefaultArchetypeTo(ApplicationDbContext ctx, Guid spaceId, Guid serverMemberId)
    {
        var everyoneId = await ctx.Archetypes
           .Where(x => x.IsDefault && x.SpaceId == spaceId)
           .Select(x => x.Id)
           .FirstAsync();

        ctx.MemberArchetypes.Add(new SpaceMemberArchetypeEntity
        {
            ArchetypeId   = everyoneId,
            SpaceMemberId = serverMemberId
        });
    }


    private async ValueTask CloneArchetypesAsync(ApplicationDbContext ctx, Guid spaceId, Guid serverMemberId, Guid userId)
    {
        var everyone = await ctx.Archetypes.AsNoTracking().FirstAsync(x => x.Id == ArchetypeEntity.DefaultArchetype_Everyone);
        var owner    = await ctx.Archetypes.AsNoTracking().FirstAsync(x => x.Id == ArchetypeEntity.DefaultArchetype_Owner);

        owner!.Id              = ArgonId.New();
        owner.CreatorId        = userId;
        owner.Space            = null!;
        owner.SpaceId          = spaceId;
        owner.SpaceMemberRoles = new List<SpaceMemberArchetypeEntity>();

        everyone!.Id              = ArgonId.New();
        everyone.CreatorId        = userId;
        everyone.SpaceId          = spaceId;
        everyone.Space            = null!;
        everyone.SpaceMemberRoles = new List<SpaceMemberArchetypeEntity>();
        everyone.IsDefault        = true;

        ctx.Archetypes.Add(everyone);
        ctx.Archetypes.Add(owner);

        ctx.MemberArchetypes.Add(new SpaceMemberArchetypeEntity
        {
            ArchetypeId   = owner.Id,
            SpaceMemberId = serverMemberId
        });

        ctx.MemberArchetypes.Add(new SpaceMemberArchetypeEntity
        {
            ArchetypeId   = everyone.Id,
            SpaceMemberId = serverMemberId
        });
    }
}
