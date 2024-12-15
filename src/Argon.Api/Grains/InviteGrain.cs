namespace Argon.Grains;

using Shared.Servers;

public class InviteGrain : Grain<ServerInviteStorage>, IInviteGrain
{
    public async ValueTask<Maybe<AcceptInviteError>> AcceptAsync(Guid userId)
    {
        if (State.InviteCode == default)
            return AcceptInviteError.NOT_FOUND;
        if (State.InviteCode.HasExpired())
            return AcceptInviteError.EXPIRED;
        await GrainFactory.GetGrain<IServerGrain>(State.InviteCode.serverId).DoJoinUserAsync(userId);
        return Maybe<AcceptInviteError>.None();
    }

    public async ValueTask<InviteCode> GetAsync()
        => State.InviteCode.code;

    public async ValueTask<bool> HasCreatedAsync()
        => State.InviteCode != default;

    public async ValueTask DropInviteCodeAsync()
        => await ClearStateAsync();

    public async ValueTask<InviteCode> EnsureAsync(Guid serverId, Guid issuer, TimeSpan expiration)
    {
        State.InviteCode = new InviteCodeEntity(new InviteCode(this.GetPrimaryKeyString()), serverId, issuer, DateTime.UtcNow + expiration, 0);
        return State.InviteCode.code;
    }
}