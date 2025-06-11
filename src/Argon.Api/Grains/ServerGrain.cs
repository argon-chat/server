namespace Argon.Grains;

using System.Diagnostics;
using System.Drawing;
using Api.Features.Utils;
using Argon.Api.Features.Bus;
using Argon.Services.L1L2;
using Consul;
using Features.Logic;
using Features.Repositories;
using Orleans.GrainDirectory;
using Persistence.States;
using Services;

[GrainDirectory(GrainDirectoryName = "servers")]
public class ServerGrain(
    [PersistentState("realtime-server", IUserSessionGrain.StorageId)]
    IPersistentState<RealtimeServerGrainState> state,
    IGrainFactory grainFactory,
    IDbContextFactory<ApplicationDbContext> context,
    IServerRepository serverRepository,
    IUserPresenceService userPresence,
    IArchetypeAgent archetypeAgent,
    ILogger<IServerGrain> logger) : Grain, IServerGrain
{
    private IArgonStream<IArgonEvent> _serverEvents;

    public async override Task OnActivateAsync(CancellationToken ct)
    {
        await state.ReadStateAsync(ct);
        state.State.UserStatuses.Clear();
        await state.WriteStateAsync(ct);

        _serverEvents = await this.Streams().CreateServerStreamFor(this.GetPrimaryKey());
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
        => state.WriteStateAsync(ct);


    public async Task<Either<Server, ServerCreationError>> CreateServer(ServerInput input, Guid creatorId)
    {
        if (string.IsNullOrEmpty(input.Name))
            return ServerCreationError.BAD_MODEL;

        await serverRepository.CreateAsync(this.GetPrimaryKey(), input, creatorId);
        await UserJoined(creatorId);
        return await GetServer();
    }

    public async Task<Server> GetServer()
    {
        await using var ctx = await context.CreateDbContextAsync();
        return await ctx.Servers
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
    }

    public async Task<Server> UpdateServer(ServerInput input)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var server = await ctx.Servers
           .FirstAsync(s => s.Id == this.GetPrimaryKey());

        var copy = server with
        {
        };
        server.Name         = input.Name ?? server.Name;
        server.Description  = input.Description ?? server.Description;
        server.AvatarFileId = input.AvatarUrl ?? server.AvatarFileId;
        ctx.Servers.Update(server);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ServerModified( /*ObjDiff.Compare(copy, server)*/[]));
        return await ctx.Servers
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
    }

    public async Task<RealtimeServerMember> GetMember(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var x = await ctx
           .UsersToServerRelations
           .Where(x => x.ServerId == this.GetPrimaryKey())
           .Where(x => x.UserId == userId)
           .Include(x => x.User)
           .Include(x => x.ServerMemberArchetypes)
           .FirstAsync();


        return new RealtimeServerMember
        {
            Member = x.ToDto(),
            Status = state.State.UserStatuses.TryGetValue(x.UserId, out var item)
                ? (item.lastSetStatus - DateTime.UtcNow).TotalMinutes < 2 ? item.Status : UserStatus.Offline
                : UserStatus.Offline,
            Presence = await userPresence.GetUsersActivityPresence(x.UserId)
        };
    }

    public async ValueTask SetUserPresence(Guid userId, UserActivityPresence presence)
        => await _serverEvents.Fire(new OnUserPresenceActivityChanged(userId, presence));

    public async ValueTask RemoveUserPresence(Guid userId)
        => await _serverEvents.Fire(new OnUserPresenceActivityRemoved(userId));

    public async Task<List<ArchetypeDto>> GetServerArchetypes()
        => await archetypeAgent.GetAllAsync(this.GetPrimaryKey());

    public async Task<ArchetypeDto> CreateArchetypeAsync(Guid creatorId, string name)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var arch = new Archetype()
        {
            ServerId      = this.GetPrimaryKey(),
            Entitlement   = ArgonEntitlement.Base,
            Id            = Guid.NewGuid(),
            Name          = name,
            Description   = "",
            IsMentionable = false,
            IsLocked      = false,
            IsHidden      = false,
            Colour        = Color.White,
            IconFileId    = null,
            CreatedAt     = DateTimeOffset.UtcNow,
            CreatorId     = creatorId,
            IsDeleted     = false,
            IsGroup       = false,
        };

        ctx.Archetypes.Add(arch);

        Debug.Assert(await ctx.SaveChangesAsync() == 1);

        await _serverEvents.Fire(new ArchetypeCreated(arch.ToDto()));

        return await archetypeAgent.DoCreatedAsync(arch);
    }

    public async Task<ArchetypeDto?> UpdateArchetypeAsync(Guid callerId, ArchetypeDto dto)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var invoker = await ctx.UsersToServerRelations
           .Where(x => x.ServerId == this.GetPrimaryKey() && x.UserId == callerId)
           .Include(x => x.ServerMemberArchetypes)
           .ThenInclude(x => x.Archetype)
           .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(dto.Name)) return null;
        if (string.IsNullOrWhiteSpace(dto.Description)) dto.Description = "";
        if (dto.Name.Length > 64) return null;
        if (dto.Description.Length > 256) return null;

        if (invoker is null)
        {
            logger.LogError(
                "User {userId} tried to change the {archetypeId} right on server {serverId}, although he is not a member of the server.",
                callerId, dto.Id, this.GetPrimaryKey()
            );
            return null;
        }

        var entity = await ctx.Archetypes.FirstOrDefaultAsync(x => x.Id == dto.Id && x.ServerId == this.GetPrimaryKey());

        if (entity is null)
        {
            logger.LogError(
                "User {userId} tried to change the {archetypeId} right on server {serverId}, but the right is not part of the server.",
                callerId, dto.Id, this.GetPrimaryKey()
            );
            return null;
        }

        var invokerArchetypes = invoker
           .ServerMemberArchetypes
           .Select(x => x.Archetype)
           .ToList();

        if (!ulong.TryParse(dto.Entitlement, out var parsed))
            return null;

        var promptedEntitlements = (ArgonEntitlement)parsed;

        var archetypeEntity = ctx.Attach(entity);
        var archetype       = archetypeEntity.Entity;

        if (archetype.Entitlement != promptedEntitlements)
        {
            if (!EntitlementEvaluator.IsAllowedToEdit(archetype, promptedEntitlements, invokerArchetypes))
            {
                logger.LogError("User {userId} is trying to edit archetype {archetypeId}, but he does not have the rights",
                    invoker.UserId, archetype.Id);
                return null;
            }

            archetype.Entitlement = promptedEntitlements;

            if (!EntitlementEvaluator.IsAllowedToEdit(archetype, invokerArchetypes))
            {
                Debug.Assert(await ctx.SaveChangesAsync() == 1);
                return await Changed(archetypeEntity.Entity);
            }
        }

        if (!EntitlementEvaluator.IsAllowedToEdit(archetype, invokerArchetypes))
            return null;

        if (!archetype.Name.Equals(dto.Name))
            archetype.Name = dto.Name;
        if (archetype.Colour.ToArgb() != dto.Colour)
            archetype.Colour = Color.FromArgb(dto.Colour);

        archetype.IsGroup       = dto.IsGroup;
        archetype.IsMentionable = dto.IsMentionable;
        archetype.UpdatedAt = DateTimeOffset.UtcNow;

        Debug.Assert(await ctx.SaveChangesAsync() == 1);
        return await Changed(archetypeEntity.Entity);


        async Task<ArchetypeDto?> Changed(Archetype value)
        {
            var result = value.ToDto();
            await archetypeAgent.DoUpdatedAsync(value);
            await _serverEvents.Fire(new ArchetypeChanged(result));
            return result;
        }
    }

    

    public async Task<List<RealtimeServerMember>> GetMembers()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var members = await ctx
           .UsersToServerRelations
           .Include(x => x.User)
           .Where(x => x.ServerId == this.GetPrimaryKey())
           .Include(x => x.ServerMemberArchetypes)
           .ToListAsync();

        var ids        = members.Select(x => x.UserId).ToList();
        var activities = await userPresence.BatchGetUsersActivityPresence(ids);

        return members.Select(x => new RealtimeServerMember
        {
            Member = x.ToDto(),
            Status = state.State.UserStatuses.TryGetValue(x.UserId, out var item)
                ? (item.lastSetStatus - DateTime.UtcNow).TotalMinutes < 15 ? item.Status : UserStatus.Offline
                : UserStatus.Offline,
            Presence = activities.TryGetValue(x.UserId, out var presence) ? presence : null
        }).ToList();
    }

    public async Task<List<RealtimeChannel>> GetChannels(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var serverMember   = await ctx.UsersToServerRelations.FirstAsync(x => x.UserId == userId && x.ServerId == this.GetPrimaryKey());
        var serverMemberId = serverMember.Id;
        var serverId       = this.GetPrimaryKey();

        var member = await ctx.UsersToServerRelations
           .Include(m => m.ServerMemberArchetypes)
           .ThenInclude(sma => sma.Archetype)
           .FirstAsync(m => m.Id == serverMemberId && m.ServerId == serverId);

        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);

        var channels = await ctx.Channels
           .Where(c => c.ServerId == serverId)
           .Include(c => c.EntitlementOverwrites)
           .ToListAsync();

        var c = channels
           .Where(c =>
            {
                var finalPerms = EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, c);
                return EntitlementAnalyzer.IsEntitlementSatisfied(finalPerms, ArgonEntitlement.ViewChannel);
            })
           .ToList();

        var results = await Task.WhenAll(c.Select(async x => new RealtimeChannel()
        {
            Channel = x,
            Users   = await grainFactory.GetGrain<IChannelGrain>(x.Id).GetMembers()
        }).ToList());

        return results.ToList();
    }

    public async ValueTask DoJoinUserAsync(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var alreadyExist = await ctx.UsersToServerRelations.AnyAsync(x => x.ServerId == this.GetPrimaryKey() && x.Id == userId);

        if (alreadyExist)
            return;
        var member = Guid.NewGuid();
        await ctx.UsersToServerRelations.AddAsync(new ServerMember
        {
            Id       = member,
            ServerId = this.GetPrimaryKey(),
            UserId   = userId
        });
        await ctx.SaveChangesAsync();

        await serverRepository.GrantDefaultArchetypeTo(ctx, this.GetPrimaryKey(), member);
        await UserJoined(userId);
    }

    public async ValueTask DoUserUpdatedAsync(Guid userId)
    {
        await using var ctx  = await context.CreateDbContextAsync();
        var             user = await ctx.Users.FirstAsync(x => x.Id == userId);
        await _serverEvents.Fire(new UserUpdated(user.ToDto()));
    }

    public async ValueTask<UserProfileDto> PrefetchProfile(Guid userId, Guid caller)
    {
        await using var ctx     = await context.CreateDbContextAsync();
        List<Guid>      userIds = [userId, caller];
        var targetMember = await ctx.Servers
           .AsNoTracking()
           .SelectMany(server => server.Users)
           .Where(member => userIds.Contains(member.UserId))
           .Include(serverMember => serverMember.User)
           .ThenInclude(user => user.Profile)
           .Include(serverMember => serverMember.ServerMemberArchetypes)
           .FirstAsync(x => x.UserId == userId);

        var profile = targetMember.User.Profile.ToDto();

        profile.Archetypes.AddRange(targetMember.ServerMemberArchetypes.ToDto());

        return profile;
    }


    public async ValueTask UserJoined(Guid userId)
    {
        await _serverEvents.Fire(new JoinToServerUser(userId));
        await SetUserStatus(userId, UserStatus.Online);
    }

    public async ValueTask SetUserStatus(Guid userId, UserStatus status)
    {
        state.State.UserStatuses[userId] = (DateTime.UtcNow, status);
        await _serverEvents.Fire(new UserChangedStatus(userId, status, []));
    }

    public async Task DeleteServer()
    {
        await using var ctx = await context.CreateDbContextAsync();
        //await ctx.Servers.DeleteByKeyAsync(this.GetPrimaryKey());
        await ctx.SaveChangesAsync();
    }

    public async Task<Channel> CreateChannel(ChannelInput input, Guid initiator)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var channel = new Channel
        {
            Name        = input.Name,
            CreatorId   = initiator,
            Description = input.Description,
            ChannelType = input.ChannelType,
            ServerId    = this.GetPrimaryKey()
        };
        await ctx.Channels.AddAsync(channel);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ChannelCreated(channel));
        return channel;
    }

    public async Task DeleteChannel(Guid channelId, Guid initiator)
    {
        await using var ctx = await context.CreateDbContextAsync();

        ctx.Channels.Remove(await ctx.Channels.FindAsync(channelId)!);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ChannelRemoved(channelId));
    }
}