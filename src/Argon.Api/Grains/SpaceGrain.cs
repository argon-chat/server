namespace Argon.Grains;

using System.Linq;
using Argon.Api.Features.Bus;
using Argon.Api.Features.Utils;
using Services.L1L2;
using Core.Services;
using Features.Logic;
using Features.MediaStorage;
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
    IEntitlementChecker entitlementChecker,
    ISystemMessageService systemMessageService,
    IKineticaFSApi kineticaFs,
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

    private async Task<ArgonSpaceBase> GetSpaceBase()
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

    public async Task SetUserPresence(Guid userId, UserActivityPresence presence)
        => await _serverEvents.Fire(new OnUserPresenceActivityChanged(this.GetPrimaryKey(), userId, presence));

    public async Task RemoveUserPresence(Guid userId)
        => await _serverEvents.Fire(new OnUserPresenceActivityRemoved(this.GetPrimaryKey(), userId));


    public async Task<List<RealtimeServerMember>> GetMembers()  
    {
        await using var ctx = await context.CreateDbContextAsync();

        var members = await ctx
           .UsersToServerRelations
           .AsNoTracking()
           .AsSplitQuery()
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
           .AsSplitQuery()
           .Include(m => m.SpaceMemberArchetypes)
           .ThenInclude(sma => sma.Archetype)
           .FirstAsync(m => m.Id == serverMemberId && m.SpaceId == spaceId);

        var basePermissions = EntitlementEvaluator.GetBasePermissions(member);

        var channels = await ctx.Channels
           .AsNoTracking()
           .AsSplitQuery()
           .Where(c => c.SpaceId == spaceId)
           .Include(c => c.EntitlementOverwrites)
           .OrderBy(c => c.ChannelGroupId)
           .ThenBy(c => c.FractionalIndex)
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

    public async Task DoJoinUserAsync()
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
        
        _ = systemMessageService.SendUserJoinedMessageAsync(spaceId, userId);
    }

    public async Task DoUserUpdatedAsync()
    {
        var userId = this.GetUserId();

        await using var ctx  = await context.CreateDbContextAsync();
        var             user = await ctx.Users.FirstAsync(x => x.Id == userId);
        await _serverEvents.Fire(new UserUpdated(this.GetPrimaryKey(), user.ToDto()));
    }

    public async Task<ArgonUserProfile> PrefetchProfile(Guid userId)
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

    public async Task SetUserStatus(Guid userId, UserStatus status)
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

    public async Task<ChannelGroupEntity> CreateChannelGroup(string name, string? description = null)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx,
            spaceId,
            callerId,
            ArgonEntitlement.ManageChannels
        );

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage channels");

        var lastGroup = await ctx.Set<ChannelGroupEntity>()
           .Where(g => g.SpaceId == spaceId)
           .OrderByDescending(g => g.FractionalIndex)
           .FirstOrDefaultAsync();

        var fractionalIndex = lastGroup != null && !string.IsNullOrEmpty(lastGroup.FractionalIndex)
            ? FractionalIndex.After(FractionalIndex.Parse(lastGroup.FractionalIndex))
            : FractionalIndex.Min();

        var group = new ChannelGroupEntity
        {
            Name            = name,
            Description     = description,
            SpaceId         = spaceId,  
            CreatorId       = callerId,
            FractionalIndex = fractionalIndex.Value
        };

        await ctx.Set<ChannelGroupEntity>().AddAsync(group);
        await ctx.SaveChangesAsync();
        
        await _serverEvents.Fire(new ChannelGroupCreated(spaceId, group.ToDto()));
        
        return group;
    }

    public async Task<ChannelGroupEntity> UpdateChannelGroup(Guid groupId, string? name = null, string? description = null, bool? isCollapsed = null, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx,
            spaceId,
            callerId,
            ArgonEntitlement.ManageChannels, ct);

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage channels");

        var group = await ctx.Set<ChannelGroupEntity>()
           .FirstOrDefaultAsync(g => g.Id == groupId && g.SpaceId == spaceId, cancellationToken: ct);

        if (group == null)
            throw new InvalidOperationException("Channel group not found");

        group.Name        = name ?? group.Name;
        group.Description = description ?? group.Description;
        if (isCollapsed.HasValue)
            group.IsCollapsed = isCollapsed.Value;

        await ctx.SaveChangesAsync(ct);
        
        await _serverEvents.Fire(new ChannelGroupModified(spaceId, group.Id, group.ToDto()), ct);
        
        return group;
    }

    public async Task MoveChannelGroup(Guid groupId, Guid? afterGroupId, Guid? beforeGroupId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx,
            spaceId,
            callerId,
            ArgonEntitlement.ManageChannels
        );

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage channels");

        var group = await ctx.Set<ChannelGroupEntity>().FindAsync(groupId);
        if (group == null || group.SpaceId != spaceId)
            return;

        FractionalIndex newIndex;

        if (afterGroupId == null && beforeGroupId == null)
        {
            var lastGroup = await ctx.Set<ChannelGroupEntity>()
               .Where(g => g.SpaceId == spaceId && g.Id != groupId)
               .OrderByDescending(g => g.FractionalIndex)
               .FirstOrDefaultAsync();

            newIndex = lastGroup != null && !string.IsNullOrEmpty(lastGroup.FractionalIndex)
                ? FractionalIndex.After(FractionalIndex.Parse(lastGroup.FractionalIndex))
                : FractionalIndex.Min();
        }
        else
        {
            var afterGroup  = afterGroupId.HasValue ? await ctx.Set<ChannelGroupEntity>().FindAsync(afterGroupId.Value) : null;
            var beforeGroup = beforeGroupId.HasValue ? await ctx.Set<ChannelGroupEntity>().FindAsync(beforeGroupId.Value) : null;

            var afterIndex  = afterGroup != null && !string.IsNullOrEmpty(afterGroup.FractionalIndex)
                ? FractionalIndex.Parse(afterGroup.FractionalIndex)
                : (FractionalIndex?)null;
            var beforeIndex = beforeGroup != null && !string.IsNullOrEmpty(beforeGroup.FractionalIndex)
                ? FractionalIndex.Parse(beforeGroup.FractionalIndex)
                : (FractionalIndex?)null;

            if (afterIndex == null && beforeIndex is { IsMin: true })
                newIndex = FractionalIndex.Min();
            else if (beforeIndex is { IsMin: true } && afterIndex != null)
                newIndex = FractionalIndex.Between(FractionalIndex.Min(), afterIndex.Value);
            else
                newIndex = FractionalIndex.Between(afterIndex, beforeIndex);
        }

        group.FractionalIndex = newIndex.Value;

        await ctx.SaveChangesAsync();
        
        await _serverEvents.Fire(new ChannelGroupReordered(spaceId, groupId, group.FractionalIndex));
    }

    public async Task DeleteChannelGroup(Guid groupId, bool deleteChannels = false)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx,
            spaceId,
            callerId,
            ArgonEntitlement.ManageChannels
        );

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage channels");

        var group = await ctx.Set<ChannelGroupEntity>()
           .Include(g => g.Channels)
           .FirstOrDefaultAsync(g => g.Id == groupId && g.SpaceId == spaceId);

        if (group == null)
            return;

        if (deleteChannels)
        {
            ctx.Set<ChannelEntity>().RemoveRange(group.Channels);
            
            foreach (var channel in group.Channels)
                await _serverEvents.Fire(new ChannelRemoved(spaceId, channel.Id));
        }
        else
            foreach (var channel in group.Channels)
                channel.ChannelGroupId = null;

        ctx.Set<ChannelGroupEntity>().Remove(group);
        await ctx.SaveChangesAsync();
        
        await _serverEvents.Fire(new ChannelGroupRemoved(spaceId, groupId));
    }

    public async Task<ChannelEntity> CreateChannel(ChannelInput input, Guid? groupId = null)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx,
            spaceId,
            callerId,
            ArgonEntitlement.ManageChannels
        );

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage channels");

        var lastChannel = await ctx.Set<ChannelEntity>()
           .Where(c => c.SpaceId == spaceId && c.ChannelGroupId == groupId)
           .OrderByDescending(c => c.FractionalIndex)
           .FirstOrDefaultAsync();

        var fractionalIndex = lastChannel != null && !string.IsNullOrEmpty(lastChannel.FractionalIndex)
            ? FractionalIndex.After(FractionalIndex.Parse(lastChannel.FractionalIndex))
            : FractionalIndex.Min();

        var channel = new ChannelEntity
        {
            Name            = input.Name,
            CreatorId       = callerId,
            Description     = input.Description,
            ChannelType     = input.ChannelType,
            SpaceId         = spaceId,
            ChannelGroupId  = groupId,
            FractionalIndex = fractionalIndex.Value
        };

        await ctx.Set<ChannelEntity>().AddAsync(channel);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ChannelCreated(spaceId, channel.ToDto()));
        return channel;
    }

    public async Task MoveChannel(Guid channelId, Guid? targetGroupId, Guid? afterChannelId, Guid? beforeChannelId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx,
            spaceId,
            callerId,
            ArgonEntitlement.ManageChannels
        );

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage channels");

        var channel = await ctx.Set<ChannelEntity>().FindAsync(channelId);
        if (channel == null || channel.SpaceId != spaceId)
            return;

        channel.ChannelGroupId = targetGroupId;

        FractionalIndex newIndex;

        if (afterChannelId == null && beforeChannelId == null)
        {
            var lastChannel = await ctx.Set<ChannelEntity>()
               .Where(c => c.SpaceId == spaceId && c.ChannelGroupId == targetGroupId && c.Id != channelId)
               .OrderByDescending(c => c.FractionalIndex)
               .FirstOrDefaultAsync();

            newIndex = lastChannel != null && !string.IsNullOrEmpty(lastChannel.FractionalIndex)
                ? FractionalIndex.After(FractionalIndex.Parse(lastChannel.FractionalIndex))
                : FractionalIndex.Min();
        }
        else
        {
            var afterChannel  = afterChannelId.HasValue ? await ctx.Set<ChannelEntity>().FindAsync(afterChannelId.Value) : null;
            var beforeChannel = beforeChannelId.HasValue ? await ctx.Set<ChannelEntity>().FindAsync(beforeChannelId.Value) : null;

            var afterIndex  = afterChannel != null && !string.IsNullOrEmpty(afterChannel.FractionalIndex)
                ? FractionalIndex.Parse(afterChannel.FractionalIndex)
                : (FractionalIndex?)null;
            var beforeIndex = beforeChannel != null && !string.IsNullOrEmpty(beforeChannel.FractionalIndex)
                ? FractionalIndex.Parse(beforeChannel.FractionalIndex)
                : (FractionalIndex?)null;

            if (afterIndex == null && beforeIndex is { IsMin: true })
                newIndex = FractionalIndex.Min();
            else if (beforeIndex is { IsMin: true } && afterIndex != null)
                newIndex = FractionalIndex.Between(FractionalIndex.Min(), afterIndex.Value);
            else
                newIndex = FractionalIndex.Between(afterIndex, beforeIndex);
        }

        channel.FractionalIndex = newIndex.Value;

        await ctx.SaveChangesAsync();
        
        await _serverEvents.Fire(new ChannelReordered(spaceId, channelId, targetGroupId, channel.FractionalIndex));
    }

    public async Task DeleteChannel(Guid channelId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx,
            spaceId,
            callerId,
            ArgonEntitlement.ManageChannels
        );

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage channels");

        var channel = await ctx.Set<ChannelEntity>().FindAsync(channelId);
        if (channel == null || channel.SpaceId != spaceId)
            return;

        ctx.Set<ChannelEntity>().Remove(channel);
        await ctx.SaveChangesAsync();
        await _serverEvents.Fire(new ChannelRemoved(spaceId, channelId));
    }

    public async Task<List<ChannelGroupEntity>> GetChannelGroups()
    {
        await using var ctx = await context.CreateDbContextAsync();

        return await ctx.Set<ChannelGroupEntity>()
           .AsNoTracking()
           .Where(g => g.SpaceId == this.GetPrimaryKey())
           .OrderBy(g => g.FractionalIndex)
           .ToListAsync();
    }

    private static uint GetLimitFor(SpaceFileKind kind)
        => kind switch
        {
            SpaceFileKind.Avatar        => 4,
            SpaceFileKind.ProfileHeader => 8,
            _                           => 2
        };

    public async ValueTask<Either<BlobId, UploadFileError>> BeginUploadSpaceFile(SpaceFileKind kind, CancellationToken ct = default)
    {
        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx,
            spaceId,
            callerId,
            ArgonEntitlement.ManageServer,
            ct);

        if (!hasPermission)
            return UploadFileError.NOT_AUTHORIZED;

        try
        {
            var result = await kineticaFs.CreateUploadUrlAsync(GetLimitFor(kind), null, ct);
            return new BlobId(result);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed upload space file {kind} for space {spaceId}", kind, spaceId);
            return UploadFileError.INTERNAL_ERROR;
        }
    }

    public async ValueTask CompleteUploadSpaceFile(Guid blobId, SpaceFileKind kind, CancellationToken ct = default)
    {
        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx,
            spaceId,
            callerId,
            ArgonEntitlement.ManageServer,
            ct);

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage server");

        var fileId = await kineticaFs.FinalizeUploadUrlAsync(blobId, ct);
        await UpdateFileIdFor(kind, fileId, ct);
    }

    private async ValueTask UpdateFileIdFor(SpaceFileKind kind, Guid fileId, CancellationToken ct = default)
    {
        var spaceId = this.GetPrimaryKey();

        await using var ctx   = await context.CreateDbContextAsync(ct);
        var             space = await ctx.Spaces.FirstAsync(x => x.Id == spaceId, cancellationToken: ct);

        switch (kind)
        {
            case SpaceFileKind.Avatar:
                space.AvatarFileId = fileId.ToString();
                break;
            case SpaceFileKind.ProfileHeader:
                space.TopBannedFileId = fileId.ToString();
                break;
        }

        await ctx.SaveChangesAsync(ct);

        var spaceBase = new ArgonSpaceBase(space.Id, space.Name, space.Description!, space.AvatarFileId, space.TopBannedFileId);
        await _serverEvents.Fire(new SpaceDetailsUpdated(spaceId, spaceBase), ct);
    }
}