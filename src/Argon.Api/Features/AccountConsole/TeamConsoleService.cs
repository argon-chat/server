namespace Argon.Api.Features.AccountConsole;

using AccountContracts;
using ion.runtime;

/// <summary>
/// Dev teams: who is in them, who has been invited, and what they own.
/// </summary>
public sealed class TeamConsoleService(ITeamAccessChecker accessChecker) : ITeamConsole
{
    private IDevTeamsGrain Teams => this.GetGrain<IDevTeamsGrain>(Guid.Empty);

    public async Task<IonArray<TeamShortDetails>> GetMyTeams(CancellationToken ct = default)
        => new(await Teams.GetMyTeamsAsync(this.GetUserId(), ct));

    public async Task<TeamDetails> GetTeamDetails(Guid teamId, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);
        return await Teams.GetTeamDetailsAsync(teamId, ct);
    }

    public async Task<IonArray<TeamInviteInfo>> GetTeamInvites(Guid teamId, CancellationToken ct = default)
    {
        await accessChecker.EnsureTeamMemberAsync(this.GetUserId(), teamId, ct);
        return new IonArray<TeamInviteInfo>(await Teams.GetTeamInvitesAsync(teamId, ct));
    }

    public Task<TeamDetails> CreateTeam(string name, CancellationToken ct = default)
        => Teams.CreateTeamAsync(this.GetUserId(), name, ct);

    public async Task<InviteUserError> InviteUserToTeam(Guid teamId, string username, CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        await accessChecker.EnsureTeamMemberAsync(userId, teamId, ct);
        return await Teams.InviteUserToTeamAsync(teamId, userId, username, TimeSpan.FromHours(24), ct);
    }

    public Task<IUploadAvatarResult> BeginUploadTeamAvatar(CancellationToken ct = default)
        => throw new NotImplementedException();

    public Task CompleteUploadTeamAvatar(Guid blobId, CancellationToken ct = default)
        => throw new NotImplementedException();

    public async Task<IonArray<MyInvitesInfo>> GetMyInvites(CancellationToken ct = default)
        => new(await Teams.GetMyInvitesAsync(this.GetUserId(), ct));

    public Task AcceptTeamInvite(Guid teamId, CancellationToken ct = default)
        => Teams.AcceptInviteAsync(this.GetUserId(), teamId, ct);

    public Task DeclineTeamInvite(Guid teamId, CancellationToken ct = default)
        => Teams.DeclineTeamInviteAsync(this.GetUserId(), teamId, ct);
}
