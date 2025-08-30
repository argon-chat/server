namespace Argon.Features.Repositories;

public interface IArchetypeRepository
{
    Task<Archetype> GetByIdAsync(Guid serverId, Guid roleId, CancellationToken ct = default);
    Task<IReadOnlyList<Archetype>> GetAllAsync(Guid serverId, CancellationToken ct = default);
}

public class ArchetypeRepository(IDbContextFactory<ApplicationDbContext> ctx) : IArchetypeRepository
{
    public async Task<Archetype> GetByIdAsync(Guid serverId, Guid roleId, CancellationToken ct = default)
    {
        await using var db = await ctx.CreateDbContextAsync(ct);
        return (await db.Archetypes.AsNoTracking().FirstAsync(x => x.SpaceId == serverId && x.Id == roleId, ct)).ToDto();
    }

    public async Task<IReadOnlyList<Archetype>> GetAllAsync(Guid serverId, CancellationToken ct = default)
    {
        await using var db = await ctx.CreateDbContextAsync(ct);
        var list = await db.Archetypes.AsNoTracking().Where(x => x.SpaceId == serverId).ToListAsync(ct);
        return list.Select(x => x.ToDto()).ToList();
    }
}