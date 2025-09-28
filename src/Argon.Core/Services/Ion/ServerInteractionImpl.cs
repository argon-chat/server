namespace Argon.Services.Ion;

using ion.runtime;
using InviteCode = ArgonContracts.InviteCode;

public class ServerInteractionImpl : IServerInteraction
{
    public async Task<IonArray<RealtimeServerMember>> GetMembers(Guid spaceId)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId)
           .GetMembers();
        return new IonArray<RealtimeServerMember>(result);
    }

    public async Task<RealtimeServerMember> GetMember(Guid spaceId, Guid userId)
        => await this
           .GetGrain<ISpaceGrain>(spaceId)
           .GetMember(userId);

    public async Task<IonArray<InviteCodeEntity>> GetInviteCodes(Guid spaceId)
    {
        var result = await this.GetGrain<IServerInvitesGrain>(spaceId)
           .GetInviteCodes();
        return new(result.Select(x
            => new InviteCodeEntity(new InviteCode(x.code.inviteCode), x.serverId, x.issuerId, x.expireTime.UtcDateTime, (ulong)x.used)));
    }

    public async Task<InviteCode> CreateInviteCode(Guid spaceId)
    {
        var result = await this
           .GetGrain<IServerInvitesGrain>(spaceId)
           .CreateInviteLinkAsync(this.GetUserId(), TimeSpan.FromDays(7));
        return new InviteCode(result.inviteCode);
    }

    public async Task<ArgonUser> PrefetchUser(Guid spaceId, Guid userId)
    {
        var result = await this.GetGrain<IUserGrain>(userId).GetMe();
        return result.ToDto();
    }

    public async Task<ArgonUserProfile> PrefetchProfile(Guid spaceId, Guid userId)
        => await this.GetGrain<ISpaceGrain>(spaceId).PrefetchProfile(userId);

    public async Task<IonArray<RealtimeChannel>> GetChannels(Guid spaceId)
        => new(await this.GetGrain<ISpaceGrain>(spaceId)
           .GetChannels());

    public async Task<IonArray<Archetype>> GetServerArchetypes(Guid spaceId)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetServerArchetypes();

    public async Task<IonArray<ArchetypeGroup>> GetDetailedServerArchetypes(Guid spaceId)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetFullyServerArchetypes();
}

public class ChannelInteractionImpl : IChannelInteraction
{
    public async Task CreateChannel(Guid spaceId, Guid channelId, CreateChannelRequest request)
        => await this
           .GetGrain<ISpaceGrain>(request.spaceId)
           .CreateChannel(new ChannelInput(request.name, request.desc, request.kind));

    public async Task DeleteChannel(Guid spaceId, Guid channelId)
        => await this
           .GetGrain<ISpaceGrain>(spaceId)
           .DeleteChannel(channelId);

    public async Task<IonArray<RealtimeChannel>> GetChannels(Guid spaceId, Guid channelId)
        => new(await this.GetGrain<ISpaceGrain>(spaceId)
           .GetChannels());

    public async Task<IonArray<ArgonMessage>> QueryMessages(Guid spaceId, Guid channelId, ulong? from, int limit)
        => IonArray<ArgonMessage>.Empty;


    public async Task<ulong> SendMessage(Guid spaceId, Guid channelId, string text, IonArray<IMessageEntity> entities,
        ulong? replyTo)
        => await this
           .GetGrain<IChannelGrain>(channelId)
           .SendMessage(text, entities.Values.ToList(), replyTo);

    public async Task<IonArray<ArgonMessage>> GetMessages(Guid spaceId, Guid channelId, int count, ulong offset)
    {
        var result = await this.GetGrain<IChannelGrain>(channelId)
           .GetMessages(count, offset);

        return result.Select(x => x.ToDto()).ToList();
    }

    public async Task DisconnectFromVoiceChannel(Guid spaceId, Guid channelId)
        => await this
           .GetGrain<IChannelGrain>(channelId)
           .Leave(this.GetUserId());

    public async Task<IInterlinkResult> Interlink(Guid spaceId, Guid channelId)
    {
        var result = await this.GetGrain<IChannelGrain>(channelId).Join();

        if (!result.IsSuccess) 
            return new FailedJoinVoice(result.Error);
        var rtc = await this.GetGrain<IChannelGrain>(channelId).GetConfiguration();
        return new SuccessJoinVoice(rtc, result.Value);
    }

    public async Task<bool> KickMemberFromChannel(Guid spaceId, Guid channelId, Guid memberId)
        => await this.GetGrain<IChannelGrain>(channelId).KickMemberFromChannel(memberId);
}