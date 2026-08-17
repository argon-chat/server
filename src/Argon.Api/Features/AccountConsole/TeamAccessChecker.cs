namespace Argon.Api.Features.AccountConsole;

using Microsoft.Extensions.Caching.Memory;

public interface ITeamAccessChecker
{
    Task EnsureTeamMemberAsync(Guid userId, Guid teamId, CancellationToken ct);
    Task EnsureTeamOwnerAsync(Guid userId, Guid teamId, CancellationToken ct);
}

/// <summary>
/// Gate for every team-scoped console call: the caller has to be in the team (or own it) before the
/// request is allowed to name a team id.
/// </summary>
/// <remarks>
/// Answers are cached in process for <see cref="CacheTtl"/>, so a membership that is revoked stays
/// usable on an already-warm console node until the entry expires.
/// </remarks>
public sealed class TeamAccessChecker(IClusterClient cluster, IMemoryCache cache) : ITeamAccessChecker
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(50);

    private IDevTeamsGrain Teams => cluster.GetGrain<IDevTeamsGrain>(Guid.Empty);

    public async Task EnsureTeamMemberAsync(Guid userId, Guid teamId, CancellationToken ct)
    {
        if (!await IsAllowed($"team_access:member:{teamId}:{userId}", () => Teams.IsUserInTeamAsync(userId, teamId, ct)))
            throw new UnauthorizedAccessException("You are not a member of this team.");
    }

    public async Task EnsureTeamOwnerAsync(Guid userId, Guid teamId, CancellationToken ct)
    {
        if (!await IsAllowed($"team_access:owner:{teamId}:{userId}", () => Teams.IsUserTeamOwnerAsync(userId, teamId, ct)))
            throw new UnauthorizedAccessException("You are not the owner of this team.");
    }

    private async Task<bool> IsAllowed(string key, Func<Task<bool>> resolve)
    {
        if (cache.TryGetValue<bool>(key, out var cached))
            return cached;

        var allowed = await resolve();
        cache.Set(key, allowed, CacheTtl);

        return allowed;
    }
}
