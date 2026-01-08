namespace Argon.Services.Ion;

using ion.runtime;
using InviteCode = ArgonContracts.InviteCode;

public class ServerInteractionImpl : IServerInteraction
{
    public async Task<IonArray<ChannelGroup>> GetChannelGroups(Guid spaceId, CancellationToken ct = default)
    {
        var groups = await this
           .GetGrain<ISpaceGrain>(spaceId)
           .GetChannelGroups();

        return new IonArray<ChannelGroup>(groups.Select(x => x.ToDto()).ToList());
    }

    public async Task<IonArray<RealtimeServerMember>> GetMembers(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId)
           .GetMembers();
        return new IonArray<RealtimeServerMember>(result);
    }

    public async Task<RealtimeServerMember> GetMember(Guid spaceId, Guid userId, CancellationToken ct = default)
        => await this
           .GetGrain<ISpaceGrain>(spaceId)
           .GetMember(userId);

    public async Task<IonArray<InviteCodeEntity>> GetInviteCodes(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IServerInvitesGrain>(spaceId)
           .GetInviteCodes();
        return new(result.Select(x
            => new InviteCodeEntity(new InviteCode(x.code.inviteCode), x.spaceId, x.issuerId, x.expireTime.UtcDateTime, (ulong)x.used)));
    }

    public async Task<InviteCode> CreateInviteCode(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this
           .GetGrain<IServerInvitesGrain>(spaceId)
           .CreateInviteLinkAsync(this.GetUserId(), TimeSpan.FromDays(7));
        return new InviteCode(result.inviteCode);
    }

    public async Task<ArgonUser> PrefetchUser(Guid spaceId, Guid userId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IUserGrain>(userId).GetMe();
        return result.ToDto();
    }

    public async Task<ArgonUserProfile> PrefetchProfile(Guid spaceId, Guid userId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).PrefetchProfile(userId);

    public async Task<IonArray<RealtimeChannel>> GetChannels(Guid spaceId, CancellationToken ct = default)
        => new(await this.GetGrain<ISpaceGrain>(spaceId)
           .GetChannels());

    public async Task<IonArray<Archetype>> GetServerArchetypes(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetServerArchetypes();

    public async Task<IonArray<ArchetypeGroup>> GetDetailedServerArchetypes(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetFullyServerArchetypes();

    public async Task<Guid> BeginUploadSpaceProfileHeader(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId).BeginUploadSpaceFile(SpaceFileKind.ProfileHeader, ct);

        if (result.IsSuccess)
            return result.Value.Id;
        throw new InvalidOperationException($"Failed to begin upload: {result.Error}");
    }

    public async Task CompleteUploadSpaceProfileHeader(Guid spaceId, Guid blobId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).CompleteUploadSpaceFile(blobId, SpaceFileKind.ProfileHeader, ct);

    public async Task<Guid> BeginUploadSpaceAvatar(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId).BeginUploadSpaceFile(SpaceFileKind.Avatar, ct);

        if (result.IsSuccess)
            return result.Value.Id;
        throw new InvalidOperationException($"Failed to begin upload: {result.Error}");
    }

    public async Task CompleteUploadSpaceAvatar(Guid spaceId, Guid blobId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).CompleteUploadSpaceFile(blobId, SpaceFileKind.Avatar, ct);
}