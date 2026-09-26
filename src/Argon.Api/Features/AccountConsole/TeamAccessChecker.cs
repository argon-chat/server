namespace Argon.Api.Features.AccountConsole;

using Argon.Features.AccountConsole;
using Microsoft.Extensions.Caching.Memory;

public interface ITeamAccessChecker
{
    Task<bool> IsTeamMemberAsync(Guid userId, Guid teamId, CancellationToken ct);
    Task<bool> IsTeamOwnerAsync(Guid userId, Guid teamId, CancellationToken ct);
}

/// <summary>
/// Gate for every team-scoped console call: the caller has to be in the team (or own it) before the
/// request is allowed to name a team id.
/// </summary>
/// <remarks>
/// Grants are cached in process for <c>accountConsole:accessCacheTtl</c>, so a membership that is
/// revoked stays usable on an already-warm console node until the entry expires. Refusals are not
/// cached: an invitee who looked before accepting would be locked out of the team they then joined.
/// </remarks>
public sealed class TeamAccessChecker(
    IClusterClient cluster,
    IMemoryCache cache,
    IOptions<AccountConsoleOptions> options) : ITeamAccessChecker
{
    private TimeSpan CacheTtl => options.Value.AccessCacheTtl;

    private IDevTeamsGrain Teams => cluster.GetGrain<IDevTeamsGrain>(Guid.Empty);

    public Task<bool> IsTeamMemberAsync(Guid userId, Guid teamId, CancellationToken ct)
        => IsAllowed($"team_access:member:{teamId}:{userId}", () => Teams.IsUserInTeamAsync(userId, teamId, ct));

    public Task<bool> IsTeamOwnerAsync(Guid userId, Guid teamId, CancellationToken ct)
        => IsAllowed($"team_access:owner:{teamId}:{userId}", () => Teams.IsUserTeamOwnerAsync(userId, teamId, ct));

    private async Task<bool> IsAllowed(string key, Func<Task<bool>> resolve)
    {
        if (cache.TryGetValue<bool>(key, out var cached))
            return cached;

        var allowed = await resolve();

        if (allowed)
            cache.Set(key, true, CacheTtl);

        return allowed;
    }
}
