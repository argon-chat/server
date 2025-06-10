namespace Argon.Features.Repositories;

public interface IArchetypeRepository
{
    Task<ArchetypeDto> GetByIdAsync(Guid serverId, Guid roleId, CancellationToken ct = default);
    Task<IReadOnlyList<ArchetypeDto>> GetAllAsync(Guid serverId, CancellationToken ct = default);
}

public class ArchetypeRepository(IDbContextFactory<ApplicationDbContext> ctx) : IArchetypeRepository
{
    public async Task<ArchetypeDto> GetByIdAsync(Guid serverId, Guid roleId, CancellationToken ct = default)
    {
        await using var db = await ctx.CreateDbContextAsync(ct);
        return await db.Archetypes.FirstAsync(x => x.ServerId == serverId && x.Id == roleId, ct).ToDto();
    }

    public async Task<IReadOnlyList<ArchetypeDto>> GetAllAsync(Guid serverId, CancellationToken ct = default)
    {
        await using var db = await ctx.CreateDbContextAsync(ct);
        var list = await db.Archetypes.Where(x => x.ServerId == serverId).ToListAsync(ct);
        return list.ToDto().AsReadOnly();
    }
}