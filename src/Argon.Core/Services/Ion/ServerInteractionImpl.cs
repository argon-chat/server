namespace Argon.Services.Ion;

using ion.runtime;
using Microsoft.Extensions.Configuration;
using InviteCode = ArgonContracts.InviteCode;

public class ServerInteractionImpl(IConfiguration configuration) : IServerInteraction
{
    private const string DefaultInviteDomain = "https://argon.gl/i";

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

    public async Task<ServerInvites> GetInviteCodes(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IServerInvitesGrain>(spaceId)
           .GetInviteCodes();
        var invites = new IonArray<InviteCodeEntity>(result.Select(x
            => new InviteCodeEntity(new InviteCode(x.code.inviteCode), x.spaceId, x.issuerId, x.expireTime.UtcDateTime,
                (ulong)x.used, x.maxUses, x.createdAt.UtcDateTime)));
        var domain = configuration["Invites:Domain"] ?? DefaultInviteDomain;
        return new ServerInvites(domain, invites);
    }

    public async Task<InviteCode> CreateInviteCode(Guid spaceId, int expireMinutes, int maxUses, CancellationToken ct = default)
    {
        // expireMinutes <= 0 means "never" — model it as a far-future timestamp so the TTL sweeper leaves it be.
        var expiration = expireMinutes <= 0
            ? TimeSpan.FromDays(365 * 100)
            : TimeSpan.FromMinutes(expireMinutes);

        var result = await this
           .GetGrain<IServerInvitesGrain>(spaceId)
           .CreateInviteLinkAsync(this.GetUserId(), expiration, maxUses);
        return new InviteCode(result.inviteCode);
    }

    public async Task RevokeInviteCode(Guid spaceId, InviteCode code, CancellationToken ct = default)
        => await this.GetGrain<IServerInvitesGrain>(spaceId).RevokeInviteAsync(code.inviteCode);

    public async Task UpdateSpaceInfo(Guid spaceId, string name, string description, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).UpdateSpace(new ServerInput(name, description, null));

    public async Task SetBoostStripHidden(Guid spaceId, bool hidden, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).SetBoostStripHidden(hidden);

    public async Task<SpaceStats> GetSpaceStats(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).GetSpaceStats();

    public async Task<ArgonUser> PrefetchUser(Guid spaceId, Guid userId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).PrefetchUser(userId, ct);

    public async Task<ArgonUserProfile> PrefetchProfile(Guid spaceId, Guid userId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).PrefetchProfile(userId);

    public async Task<IonArray<RealtimeChannel>> GetChannels(Guid spaceId, CancellationToken ct = default)
        => new(await this.GetGrain<ISpaceGrain>(spaceId)
           .GetChannels());

    public async Task<IonArray<Archetype>> GetServerArchetypes(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetServerArchetypes();

    public async Task<IonArray<ArchetypeGroup>> GetDetailedServerArchetypes(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetFullyServerArchetypes();

    public async Task<IUploadFileResult> BeginUploadSpaceProfileHeader(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId).BeginUploadSpaceFile(SpaceFileKind.ProfileHeader, ct);

        if (result.IsSuccess)
        {
            var t = result.Value;
            return new SuccessUploadFile(t.BlobId, t.Url, UploadHelpers.ToFormFields(t.Fields), t.TtlSeconds);
        }
        return new FailedUploadFile(result.Error);
    }

    public async Task CompleteUploadSpaceProfileHeader(Guid spaceId, Guid blobId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).CompleteUploadSpaceFile(blobId, SpaceFileKind.ProfileHeader, ct);

    public async Task<IUploadFileResult> BeginUploadSpaceAvatar(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId).BeginUploadSpaceFile(SpaceFileKind.Avatar, ct);

        if (result.IsSuccess)
        {
            var t = result.Value;
            return new SuccessUploadFile(t.BlobId, t.Url, UploadHelpers.ToFormFields(t.Fields), t.TtlSeconds);
        }
        return new FailedUploadFile(result.Error);
    }

    public async Task CompleteUploadSpaceAvatar(Guid spaceId, Guid blobId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).CompleteUploadSpaceFile(blobId, SpaceFileKind.Avatar, ct);

    public async Task<IUploadFileResult> BeginUploadInviteImage(Guid spaceId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId).BeginUploadSpaceFile(SpaceFileKind.InviteImage, ct);

        if (result.IsSuccess)
        {
            var t = result.Value;
            return new SuccessUploadFile(t.BlobId, t.Url, UploadHelpers.ToFormFields(t.Fields), t.TtlSeconds);
        }
        return new FailedUploadFile(result.Error);
    }

    public async Task CompleteUploadInviteImage(Guid spaceId, Guid blobId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceGrain>(spaceId).CompleteUploadSpaceFile(blobId, SpaceFileKind.InviteImage, ct);
}
