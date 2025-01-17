namespace Argon.Grains;

using Shared.Servers;

public class InviteGrain(
    [PersistentState("invites-store", IServerInvitesGrain.StorageId)]
    IPersistentState<ServerInviteStorage> state) : Grain, IInviteGrain
{
    public async ValueTask<Maybe<AcceptInviteError>> AcceptAsync(Guid userId)
    {
        await state.ReadStateAsync();
        if (state.State.InviteCode == default)
            return AcceptInviteError.NOT_FOUND;
        if (state.State.InviteCode.HasExpired())
            return AcceptInviteError.EXPIRED;
        await GrainFactory.GetGrain<IServerGrain>(state.State.InviteCode.serverId).DoJoinUserAsync(userId);
        return Maybe<AcceptInviteError>.None();
    }

    public async ValueTask<InviteCodeEntity> GetAsync()
        => state.State.InviteCode;

    public async ValueTask<bool> HasCreatedAsync()
        => state.State.InviteCode != default;

    public async ValueTask DropInviteCodeAsync()
        => await state.ClearStateAsync();

    public async ValueTask<InviteCode> EnsureAsync(Guid serverId, Guid issuer, TimeSpan expiration)
    {
        state.State.InviteCode = new InviteCodeEntity(new InviteCode(this.GetPrimaryKeyString()), serverId, issuer, DateTime.UtcNow + expiration, 0);
        await state.WriteStateAsync();
        return state.State.InviteCode.code;
    }
}