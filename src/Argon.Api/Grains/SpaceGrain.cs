namespace Argon.Grains;

using System.Linq;
using Argon.Api.Features.Bus;
using Argon.Services.L1L2;
using Features.Logic;
using Features.Repositories;
using ion.runtime;
using Orleans.GrainDirectory;
using Persistence.States;

[GrainDirectory(GrainDirectoryName = "servers")]
public class SpaceGrain(
    [PersistentState("realtime-server", IUserSessionGrain.StorageId)]
    IPersistentState<RealtimeServerGrainState> state,
    IGrainFactory grainFactory,
    IDbContextFactory<ApplicationDbContext> context,
    IServerRepository serverRepository,
    IUserPresenceService userPresence,
    IArchetypeAgent archetypeAgent,
    ILogger<ISpaceGrain> logger) : Grain, ISpaceGrain
{
    private IDistributedArgonStream<IArgonEvent> _serverEvents;

    public async override Task OnActivateAsync(CancellationToken ct)
    {
        await state.ReadStateAsync(ct);
        state.State.UserStatuses.Clear();
        await state.WriteStateAsync(ct);

        _serverEvents = await this.Streams().CreateServerStreamFor(this.GetPrimaryKey());
    }

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
    {
        await _serverEvents.DisposeAsync();
        await state.WriteStateAsync(ct);
    }

    public async Task<Either<ArgonSpaceBase, ServerCreationError>> CreateSpace(ServerInput input)
    {
        if (string.IsNullOrEmpty(input.Name))
            return ServerCreationError.BAD_MODEL;
        var creatorId = this.GetUserId();

        await serverRepository.CreateAsync(this.GetPrimaryKey(), input, creatorId);
        await UserJoined(creatorId);
        return await GetSpaceBase();
    }

    public async Task<ArgonSpaceBase> GetSpaceBase()
    {
        await using var ctx = await context.CreateDbContextAsync();
        var result = await ctx.Spaces
           .AsNoTracking()
           .Select(x => new
            {
                x.Id,
                x.Name,
                x.Description,
                x.AvatarFileId,
                x.TopBannedFileId
            })
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
        return new ArgonSpaceBase(result.Id, result.Name, result.Description!, result.AvatarFileId, result.TopBannedFileId);
    }

    public async Task<SpaceEntity> GetSpace()
    {
        await using var ctx = await context.CreateDbContextAsync();
        return await ctx.Spaces
           .AsNoTracking()
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
    }

    public async Task<SpaceEntity> UpdateSpace(ServerInput input)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var server = await ctx.Spaces
           .FirstAsync(s => s.Id == this.GetPrimaryKey());

        server.Name         = input.Name ?? server.Name;
        server.Description  = input.Description ?? server.Description;
        server.AvatarFileId = input.AvatarUrl ?? server.AvatarFileId;
        
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ServerModified(this.GetPrimaryKey(), IonArray<string>.Empty));
        return server;
    }

    public async Task<RealtimeServerMember> GetMember(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var x = await ctx
           .UsersToServerRelations
           .AsNoTracking()
           .Where(x => x.SpaceId == this.GetPrimaryKey())
           .Where(x => x.UserId == userId)
           .Include(x => x.User)
           .Include(x => x.SpaceMemberArchetypes)
           .FirstAsync();

        var status = state.State.UserStatuses.TryGetValue(x.UserId, out var item)
            ? (item.lastSetStatus - DateTime.UtcNow).TotalMinutes < 2 ? item.Status : UserStatus.Offline
            : UserStatus.Offline;
        var presence = await userPresence.GetUsersActivityPresence(x.UserId);

        return new RealtimeServerMember(x.ToDto(), status, presence);
    }

    public async ValueTask SetUserPresence(Guid userId, UserActivityPresence presence)
        => await _serverEvents.Fire(new OnUserPresenceActivityChanged(this.GetPrimaryKey(), userId, presence));

    public async ValueTask RemoveUserPresence(Guid userId)
        => await _serverEvents.Fire(new OnUserPresenceActivityRemoved(this.GetPrimaryKey(), userId));


    public async Task<List<RealtimeServerMember>> GetMembers()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var members = await ctx
           .UsersToServerRelations
           .AsNoTracking()
           .Include(x => x.User)
           .Where(x => x.SpaceId == this.GetPrimaryKey())
           .Include(x => x.SpaceMemberArchetypes)
           .ToListAsync();

        var ids        = members.Select(x => x.UserId).ToList();
        var activities = await userPresence.BatchGetUsersActivityPresence(ids);

        return members.Select(x => new RealtimeServerMember(x.ToDto(), state.State.UserStatuses.TryGetValue(x.UserId, out var item)
            ? (item.lastSetStatus - DateTime.UtcNow).TotalMinutes < 15 ? item.Status : UserStatus.Offline
            : UserStatus.Offline, (activities.TryGetValue(x.UserId, out var presence) ? presence : null))).ToList();
    }

    public async Task<List<RealtimeChannel>> GetChannels()
    {
        var             callerId = this.GetUserId();
        await using var ctx      = await context.CreateDbContextAsync();

        var serverMember   = await ctx.UsersToServerRelations.FirstAsync(x => x.UserId == callerId && x.SpaceId == this.GetPrimaryKey());
        var serverMemberId = serverMember.Id;
        var spaceId        = this.GetPrimaryKey();

        var member = await ctx.UsersToServerRelations
           .AsNoTracking()
           .Include(m => m.SpaceMemberArchetypes)
           .ThenInclude(sma => sma.Archetype)
           .FirstAsync(m => m.Id == serverMemberId && m.SpaceId == spaceId);

        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);

        var channels = await ctx.Channels
           .Where(c => c.SpaceId == spaceId)
           .Include(c => c.EntitlementOverwrites)
           .ToListAsync();

        var c = channels
           .Where(c =>
            {
                var finalPerms = EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, c);
                return EntitlementAnalyzer.IsEntitlementSatisfied(finalPerms, ArgonEntitlement.ViewChannel);
            })
           .ToList();

        var results = await Task.WhenAll(c
           .Select(async x => new RealtimeChannel(x.ToDto(), new(await grainFactory.GetGrain<IChannelGrain>(x.Id).GetMembers()))).ToList());

        return results.ToList();
    }

    public async ValueTask DoJoinUserAsync()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var userId  = this.GetUserId();
        var spaceId = this.GetPrimaryKey();

        var exists = await ctx.UsersToServerRelations
           .AnyAsync(x => x.SpaceId == spaceId && x.UserId == userId);

        if (exists)
            return;

        var member = Guid.NewGuid();
        await ctx.UsersToServerRelations.AddAsync(new SpaceMemberEntity
        {
            Id      = member,
            SpaceId = spaceId,
            UserId  = userId
        });
        await ctx.SaveChangesAsync();

        await serverRepository.GrantDefaultArchetypeTo(ctx, spaceId, member);
        await UserJoined(userId);
    }

    public async ValueTask DoUserUpdatedAsync()
    {
        var userId = this.GetUserId();

        await using var ctx  = await context.CreateDbContextAsync();
        var             user = await ctx.Users.FirstAsync(x => x.Id == userId);
        await _serverEvents.Fire(new UserUpdated(this.GetPrimaryKey(), user.ToDto()));
    }

    public async ValueTask<ArgonUserProfile> PrefetchProfile(Guid userId)
    {
        var caller = this.GetUserId();

        await using var ctx     = await context.CreateDbContextAsync();
        List<Guid>      userIds = [userId, caller];
        var targetMember = await ctx.Spaces
           .AsNoTracking()
           .SelectMany(server => server.Users)
           .Where(member => userIds.Contains(member.UserId))
           .Include(serverMember => serverMember.User)
           .ThenInclude(user => user.Profile)
           .Include(serverMember => serverMember.SpaceMemberArchetypes)
           .FirstAsync(x => x.UserId == userId);

        return targetMember.User.Profile.ToDto() with
        {
            archetypes = new(targetMember.SpaceMemberArchetypes.Select(x => x.ToDto()))
        };
    }


    public async ValueTask UserJoined(Guid userId)
    {
        await _serverEvents.Fire(new JoinToServerUser(this.GetPrimaryKey(), userId));
        await SetUserStatus(userId, UserStatus.Online);
    }

    public async ValueTask SetUserStatus(Guid userId, UserStatus status)
    {
        state.State.UserStatuses[userId] = (DateTime.UtcNow, status);
        await _serverEvents.Fire(new UserChangedStatus(this.GetPrimaryKey(), userId, status, new IonArray<string>([""])));
    }

    public async Task DeleteSpace()
    {
        await using var ctx = await context.CreateDbContextAsync();
        //await ctx.Servers.DeleteByKeyAsync(this.GetPrimaryKey());
        await ctx.SaveChangesAsync();
    }

    public async Task<ChannelEntity> CreateChannel(ChannelInput input)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var channel = new ChannelEntity
        {
            Name            = input.Name,
            CreatorId       = this.GetUserId(),
            Description     = input.Description,
            ChannelType     = input.ChannelType,
            SpaceId         = this.GetPrimaryKey(),
            FractionalIndex = ""
        };
        await ctx.Channels.AddAsync(channel);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ChannelCreated(this.GetPrimaryKey(), channel.ToDto()));
        return channel;
    }

    public async Task DeleteChannel(Guid channelId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        ctx.Channels.Remove(await ctx.Channels.FindAsync(channelId)!);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ChannelRemoved(this.GetPrimaryKey(), channelId));
    }


    private async Task<bool> HasAccessAsync(ApplicationDbContext ctx, Guid callerId, ArgonEntitlement requiredEntitlement)
    {
        var invoker = await ctx.UsersToServerRelations
           .AsNoTracking()
           .Where(x => x.SpaceId == this.GetPrimaryKey() && x.UserId == callerId)
           .Include(x => x.SpaceMemberArchetypes)
           .ThenInclude(x => x.Archetype)
           .FirstOrDefaultAsync();

        if (invoker is null)
            return false;

        var invokerArchetypes = invoker
           .SpaceMemberArchetypes
           .Select(x => x.Archetype)
           .ToList();

        return invokerArchetypes.Any(x
            => x.Entitlement.HasFlag(requiredEntitlement));
    }
}