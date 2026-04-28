namespace Argon.Grains;

using Argon.Api.Features.Bus;
using Argon.Api.Features.Utils;
using Argon.Core.Entities.Data;
using Argon.Core.Features.Transport;
using Argon.Features.BotApi;
using Core.Services;
using Features.Logic;
using Features.MediaStorage;
using Features.Repositories;
using ion.runtime;
using Orleans.GrainDirectory;
using Persistence.States;
using Services.L1L2;
using System.Linq;

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
    AppHubServer appHubServer,
    BotEventPublisher botEventPublisher,
    ILogger<ISpaceGrain> logger) : Grain, ISpaceGrain
{

    private Task Fire<T>(T ev, CancellationToken ct = default) where T : IArgonEvent
        => appHubServer.BroadcastSpace(ev, this.GetPrimaryKey(), ct);

    public async override Task OnActivateAsync(CancellationToken ct)
    {
        await state.ReadStateAsync(ct);
    }

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
        => await state.WriteStateAsync(ct);

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
                x.TopBannedFileId,
                x.BoostCount,
                x.BoostLevel
            })
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
        return new ArgonSpaceBase(result.Id, result.Name, result.Description!, result.AvatarFileId, result.TopBannedFileId, result.BoostCount, result.BoostLevel);
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
        await Fire(new ServerModified(this.GetPrimaryKey(), IonArray<string>.Empty));
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

        var status   = await userPresence.GetAggregatedStatusAsync(x.UserId);
        var presence = await userPresence.GetUsersActivityPresence(x.UserId);

        return new RealtimeServerMember(x.ToDto(), status, presence);
    }

    public async Task SetUserPresence(Guid userId, UserActivityPresence presence)
        => await Fire(new OnUserPresenceActivityChanged(this.GetPrimaryKey(), userId, presence));

    public async Task RemoveUserPresence(Guid userId)
        => await Fire(new OnUserPresenceActivityRemoved(this.GetPrimaryKey(), userId));


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

        var ids        = members.Select(x => x.UserId).Distinct().ToList();
        var statuses   = await userPresence.BatchGetAggregatedStatusAsync(ids);
        var activities = await userPresence.BatchGetUsersActivityPresence(ids);

        return members.Select(x => new RealtimeServerMember(
            x.ToDto(),
            statuses.TryGetValue(x.UserId, out var s) ? s : UserStatus.Offline,
            activities.TryGetValue(x.UserId, out var presence) ? presence : null)).ToList();
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

        var channelsFiltered = channels
           .Where(c =>
            {
                var finalPerms = EntitlementEvaluator.ApplyPermissionOverwrites(basePermissions, member, c);
                return EntitlementAnalyzer.IsEntitlementSatisfied(finalPerms, ArgonEntitlement.ViewChannel);
            })
           .ToList();

        // Batch fetch realtime state for all channels in parallel
        var states = await Task.WhenAll(channelsFiltered
           .Select(x => grainFactory.GetGrain<IChannelGrain>(x.Id).GetRealtimeStateAsync()));

        var results = channelsFiltered
           .Zip(states, (ch, s) => new RealtimeChannel(ch.ToDto(), new(s.Members), s.MeetingInfo))
           .ToList();

        return results;
    }

    public Task DoJoinUserAsync()
        => AddMemberAsync(this.GetUserId());

    private async Task AddMemberAsync(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();

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
        await Fire(new UserUpdated(this.GetPrimaryKey(), user.ToDto()));
    }

    /// <summary>
    /// Prefix for ephemeral guest user IDs from meetings.
    /// </summary>
    private static readonly byte[] GuestIdPrefix = [0xFA, 0xFC, 0xCC, 0xCC];

    private static bool IsGuestUserId(Guid userId)
    {
        Span<byte> bytes = stackalloc byte[16];
        userId.TryWriteBytes(bytes);
        return bytes[..4].SequenceEqual(GuestIdPrefix);
    }


    public async Task<ArgonUserProfile> PrefetchProfile(Guid userId)
    {
        var caller = this.GetUserId();

        if (IsGuestUserId(userId))
            return new ArgonUserProfile(userId, null, null, null, null, "Guest User", IonArray<string>.Empty,
                IonArray<SpaceMemberArchetype>.Empty);

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

    public async Task<ArgonUser> PrefetchUser(Guid userId, CancellationToken ct = default)
    {
        if (IsGuestUserId(userId))
            return new ArgonUser(userId, "guest", "Guest User", null, UserFlag.NONE);

        await using var ctx = await context.CreateDbContextAsync(ct);
        
        var user = await ctx.Users
           .Include(x => x.BotEntity)
           .AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => new ArgonUser(u.Id, u.Username, u.DisplayName, u.AvatarFileId, UserEntity.GetFlags(u)))
           .FirstOrDefaultAsync(ct);

        if (user is null)
            return new ArgonUser(userId, "unknown", "Unknown User", null, UserFlag.NONE);

        return user;
    }


    public async ValueTask UserJoined(Guid userId)
    {
        await Fire(new JoinToServerUser(this.GetPrimaryKey(), userId));
        await SetUserStatus(userId, UserStatus.Online);
    }

    public async Task SetUserStatus(Guid userId, UserStatus status)
    {
        await Fire(new UserChangedStatus(this.GetPrimaryKey(), userId, status, new IonArray<string>([""])));
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

        await Fire(new ChannelGroupCreated(spaceId, group.ToDto()));

        return group;
    }

    public async Task<ChannelGroupEntity> UpdateChannelGroup(Guid groupId, string? name = null, string? description = null, bool? isCollapsed = null,
        CancellationToken ct = default)
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

        await Fire(new ChannelGroupModified(spaceId, group.Id, group.ToDto()), ct);

        return group;
    }

    private const int RebalanceThreshold = 20;

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
            var afterGroup = afterGroupId.HasValue
                ? await ctx.Set<ChannelGroupEntity>().FirstOrDefaultAsync(g => g.Id == afterGroupId.Value && g.SpaceId == spaceId)
                : null;
            var beforeGroup = beforeGroupId.HasValue
                ? await ctx.Set<ChannelGroupEntity>().FirstOrDefaultAsync(g => g.Id == beforeGroupId.Value && g.SpaceId == spaceId)
                : null;

            var afterIndex = afterGroup != null && !string.IsNullOrEmpty(afterGroup.FractionalIndex)
                ? FractionalIndex.Parse(afterGroup.FractionalIndex)
                : (FractionalIndex?)null;
            var beforeIndex = beforeGroup != null && !string.IsNullOrEmpty(beforeGroup.FractionalIndex)
                ? FractionalIndex.Parse(beforeGroup.FractionalIndex)
                : (FractionalIndex?)null;

            if (afterIndex != null && beforeIndex != null && afterIndex.Value.CompareTo(beforeIndex.Value) >= 0)
                return;

            if (afterIndex == null && beforeIndex is { IsMin: true })
            {
                var nextGroup = await ctx.Set<ChannelGroupEntity>()
                   .Where(g => g.SpaceId == spaceId && g.Id != groupId && g.Id != beforeGroup!.Id)
                   .Where(g => string.Compare(g.FractionalIndex, beforeGroup!.FractionalIndex) > 0)
                   .OrderBy(g => g.FractionalIndex)
                   .FirstOrDefaultAsync();

                beforeGroup!.FractionalIndex = nextGroup != null && !string.IsNullOrEmpty(nextGroup.FractionalIndex)
                    ? FractionalIndex.Between(beforeIndex.Value, FractionalIndex.Parse(nextGroup.FractionalIndex)).Value
                    : FractionalIndex.After(beforeIndex.Value).Value;

                newIndex = FractionalIndex.Min();
            }
            else
            {
                newIndex = FractionalIndex.Between(afterIndex, beforeIndex);
            }
        }

        group.FractionalIndex = newIndex.Value;

        if (group.FractionalIndex.Length > RebalanceThreshold)
            await RebalanceGroupOrder(ctx, spaceId);

        await ctx.SaveChangesAsync();

        await Fire(new ChannelGroupReordered(spaceId, groupId, group.FractionalIndex));
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
                await Fire(new ChannelRemoved(spaceId, channel.Id));
        }
        else
            foreach (var channel in group.Channels)
                channel.ChannelGroupId = null;

        ctx.Set<ChannelGroupEntity>().Remove(group);
        await ctx.SaveChangesAsync();

        await Fire(new ChannelGroupRemoved(spaceId, groupId));
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
        await Fire(new ChannelCreated(spaceId, channel.ToDto()));
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
            var afterChannel = afterChannelId.HasValue
                ? await ctx.Set<ChannelEntity>().FirstOrDefaultAsync(c => c.Id == afterChannelId.Value && c.SpaceId == spaceId)
                : null;
            var beforeChannel = beforeChannelId.HasValue
                ? await ctx.Set<ChannelEntity>().FirstOrDefaultAsync(c => c.Id == beforeChannelId.Value && c.SpaceId == spaceId)
                : null;

            var afterIndex = afterChannel != null && !string.IsNullOrEmpty(afterChannel.FractionalIndex)
                ? FractionalIndex.Parse(afterChannel.FractionalIndex)
                : (FractionalIndex?)null;
            var beforeIndex = beforeChannel != null && !string.IsNullOrEmpty(beforeChannel.FractionalIndex)
                ? FractionalIndex.Parse(beforeChannel.FractionalIndex)
                : (FractionalIndex?)null;

            if (afterIndex != null && beforeIndex != null && afterIndex.Value.CompareTo(beforeIndex.Value) >= 0)
                return;

            if (afterIndex == null && beforeIndex is { IsMin: true })
            {
                var nextChannel = await ctx.Set<ChannelEntity>()
                   .Where(c => c.SpaceId == spaceId && c.ChannelGroupId == targetGroupId && c.Id != channelId && c.Id != beforeChannel!.Id)
                   .Where(c => string.Compare(c.FractionalIndex, beforeChannel!.FractionalIndex) > 0)
                   .OrderBy(c => c.FractionalIndex)
                   .FirstOrDefaultAsync();

                beforeChannel!.FractionalIndex = nextChannel != null && !string.IsNullOrEmpty(nextChannel.FractionalIndex)
                    ? FractionalIndex.Between(beforeIndex.Value, FractionalIndex.Parse(nextChannel.FractionalIndex)).Value
                    : FractionalIndex.After(beforeIndex.Value).Value;

                newIndex = FractionalIndex.Min();
            }
            else
            {
                newIndex = FractionalIndex.Between(afterIndex, beforeIndex);
            }
        }

        channel.FractionalIndex = newIndex.Value;

        if (channel.FractionalIndex.Length > RebalanceThreshold)
            await RebalanceChannelOrder(ctx, spaceId, targetGroupId);

        await ctx.SaveChangesAsync();

        await Fire(new ChannelReordered(spaceId, channelId, targetGroupId, channel.FractionalIndex));
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
        await Fire(new ChannelRemoved(spaceId, channelId));
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

        var fileInfo = await kineticaFs.FinalizeUploadUrlAsync(blobId, ct);
        await UpdateFileIdFor(kind, fileInfo.FileId, ct);
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

        var spaceBase = new ArgonSpaceBase(space.Id, space.Name, space.Description!, space.AvatarFileId, space.TopBannedFileId, space.BoostCount, space.BoostLevel);
        await Fire(new SpaceDetailsUpdated(spaceId, spaceBase), ct);
    }

    private static async Task RebalanceGroupOrder(ApplicationDbContext ctx, Guid spaceId)
    {
        var items = await ctx.Set<ChannelGroupEntity>()
           .Where(g => g.SpaceId == spaceId)
           .ToListAsync();
        items.Sort((a, b) => string.Compare(a.FractionalIndex, b.FractionalIndex, StringComparison.Ordinal));
        var indices = FractionalIndex.Distribute(items.Count);
        for (var i = 0; i < items.Count; i++)
            items[i].FractionalIndex = indices[i].Value;
    }

    private static async Task RebalanceChannelOrder(ApplicationDbContext ctx, Guid spaceId, Guid? groupId)
    {
        var items = await ctx.Set<ChannelEntity>()
           .Where(c => c.SpaceId == spaceId && c.ChannelGroupId == groupId)
           .ToListAsync();
        items.Sort((a, b) => string.Compare(a.FractionalIndex, b.FractionalIndex, StringComparison.Ordinal));
        var indices = FractionalIndex.Distribute(items.Count);
        for (var i = 0; i < items.Count; i++)
            items[i].FractionalIndex = indices[i].Value;
    }

    // ───────────── Bot management ─────────────

    public async Task<List<InstalledBotRecord>> GetInstalledBots()
    {
        await using var ctx = await context.CreateDbContextAsync();
        var spaceId = this.GetPrimaryKey();

        // Join: members → bots → users → locked archetypes (for entitlements)
        var botsRaw = await ctx.UsersToServerRelations
           .AsNoTracking()
           .Where(m => m.SpaceId == spaceId)
           .Join(ctx.BotEntities.AsNoTracking(),
                m => m.UserId,
                b => b.BotAsUserId,
                (m, b) => new { Member = m, Bot = b })
           .Join(ctx.Users.AsNoTracking(),
                x => x.Bot.BotAsUserId,
                u => u.Id,
                (x, u) => new { x.Member, x.Bot, User = u })
           .ToListAsync();

        if (botsRaw.Count == 0)
            return [];

        // Bulk-query locked archetypes assigned to bot members in this space
        var memberIds = botsRaw.Select(x => x.Member.Id).ToList();
        var grantedMap = await ctx.Set<ArchetypeEntity>()
           .AsNoTracking()
           .Where(a => a.IsLocked && a.SpaceId == spaceId)
           .Join(ctx.Set<SpaceMemberArchetypeEntity>().AsNoTracking()
                    .Where(sma => memberIds.Contains(sma.SpaceMemberId)),
                a => a.Id,
                sma => sma.ArchetypeId,
                (a, sma) => new { sma.SpaceMemberId, a.Entitlement })
           .ToDictionaryAsync(x => x.SpaceMemberId, x => x.Entitlement);

        return botsRaw.Select(x =>
        {
            var granted = grantedMap.GetValueOrDefault(x.Member.Id);
            var pending = (x.Bot.RequiredEntitlements & ~granted) != 0;
            return new InstalledBotRecord(
                x.Bot.AppId, x.Bot.Name, x.User.Username, x.User.AvatarFileId,
                x.Bot.IsVerified, x.Bot.BotAsUserId,
                x.Bot.RequiredEntitlements, granted, pending);
        }).ToList();
    }

    public async Task<InstallBotGrainResult> InstallBot(Guid botAppId)
    {
        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        // Verify caller is the space owner
        var space = await ctx.Spaces
           .AsNoTracking()
           .Where(s => s.Id == spaceId)
           .Select(s => new { s.CreatorId })
           .FirstOrDefaultAsync();

        if (space is null)
            return new InstallBotGrainResult(false, InstallBotError.NOT_FOUND);

        if (space.CreatorId != callerId)
            return new InstallBotGrainResult(false, InstallBotError.INSUFFICIENT_PERMISSIONS);

        // Look up the bot
        var bot = await ctx.BotEntities
           .AsNoTracking()
           .Where(b => b.AppId == botAppId)
           .Select(b => new { b.BotAsUserId, b.Name, b.IsVerified, b.MaxSpaces, b.LifecycleState, b.RequiredEntitlements })
           .FirstOrDefaultAsync();

        if (bot is null || bot.LifecycleState != BotLifecycleState.Published)
            return new InstallBotGrainResult(false, InstallBotError.NOT_FOUND);

        // Check already installed
        var alreadyInstalled = await ctx.UsersToServerRelations
           .AnyAsync(x => x.SpaceId == spaceId && x.UserId == bot.BotAsUserId);

        if (alreadyInstalled)
            return new InstallBotGrainResult(false, InstallBotError.ALREADY_INSTALLED);

        // Check bot's max space limit
        if (bot.MaxSpaces > 0)
        {
            var currentCount = await ctx.UsersToServerRelations
               .CountAsync(x => x.UserId == bot.BotAsUserId);
            if (currentCount >= bot.MaxSpaces)
                return new InstallBotGrainResult(false, InstallBotError.BOT_SPACE_LIMIT);
        }

        // Join the bot-as-user to the space directly, no RequestContext hacks
        await AddMemberAsync(bot.BotAsUserId);

        // Create a locked archetype with the bot's required entitlements
        var botArchetype = new ArchetypeEntity
        {
            Id          = Guid.NewGuid(),
            SpaceId     = spaceId,
            CreatorId   = callerId,
            Name        = $"Bot: {bot.Name}",
            Description = $"Auto-created archetype for bot {bot.Name}",
            Entitlement = bot.RequiredEntitlements,
            IsLocked    = true,
            IsHidden    = true,
            IsGroup     = false,
            IsDefault   = false,
        };
        ctx.Set<ArchetypeEntity>().Add(botArchetype);

        // Assign archetype to the bot member
        var botMember = await ctx.UsersToServerRelations
           .Where(m => m.SpaceId == spaceId && m.UserId == bot.BotAsUserId)
           .Select(m => new { m.Id })
           .FirstAsync();

        ctx.Set<SpaceMemberArchetypeEntity>().Add(new SpaceMemberArchetypeEntity
        {
            SpaceMemberId = botMember.Id,
            ArchetypeId   = botArchetype.Id,
        });
        await ctx.SaveChangesAsync();

        // Notify bot gateway about new space subscription
        var gateway = grainFactory.GetGrain<IBotGatewayGrain>(bot.BotAsUserId);
        if (await gateway.IsConnectedAsync())
            await gateway.SubscribeToSpace(spaceId);

        // Publish lifecycle event to the bot
        await botEventPublisher.PublishBotLifecycleAsync(bot.BotAsUserId,
            BotEventType.BotInstallingToSpace, new BotInstallingToSpaceEvent(spaceId));

        // Fetch user for response
        var botUser = await ctx.Users
           .AsNoTracking()
           .Where(u => u.Id == bot.BotAsUserId)
           .Select(u => new { u.Username, u.AvatarFileId })
           .FirstAsync();

        return new InstallBotGrainResult(true, Bot: new InstalledBotRecord(
            botAppId, bot.Name, botUser.Username, botUser.AvatarFileId, bot.IsVerified, bot.BotAsUserId,
            bot.RequiredEntitlements, bot.RequiredEntitlements, PendingApproval: false));
    }

    public async Task<UninstallBotGrainResult> UninstallBot(Guid botAppId)
    {
        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        // Verify caller is the space owner
        var space = await ctx.Spaces
           .AsNoTracking()
           .Where(s => s.Id == spaceId)
           .Select(s => new { s.CreatorId })
           .FirstOrDefaultAsync();

        if (space is null)
            return new UninstallBotGrainResult(false, UninstallBotError.NOT_FOUND);

        if (space.CreatorId != callerId)
            return new UninstallBotGrainResult(false, UninstallBotError.INSUFFICIENT_PERMISSIONS);

        // Find the bot
        var bot = await ctx.BotEntities
           .AsNoTracking()
           .Where(b => b.AppId == botAppId)
           .Select(b => new { b.BotAsUserId })
           .FirstOrDefaultAsync();

        if (bot is null)
            return new UninstallBotGrainResult(false, UninstallBotError.NOT_FOUND);

        // Find the bot's space member
        var botMember = await ctx.UsersToServerRelations
           .Where(x => x.SpaceId == spaceId && x.UserId == bot.BotAsUserId)
           .Select(x => new { x.Id })
           .FirstOrDefaultAsync();

        if (botMember is null)
            return new UninstallBotGrainResult(false, UninstallBotError.NOT_INSTALLED);

        // Collect archetype IDs assigned to the bot member
        var archetypeIds = await ctx.Set<SpaceMemberArchetypeEntity>()
           .Where(x => x.SpaceMemberId == botMember.Id)
           .Select(x => x.ArchetypeId)
           .Distinct()
           .ToListAsync();

        // Delete archetype assignments
        await ctx.Set<SpaceMemberArchetypeEntity>()
           .Where(x => x.SpaceMemberId == botMember.Id)
           .ExecuteDeleteAsync();

        // Delete the bot's locked archetypes
        if (archetypeIds.Count > 0)
            await ctx.Set<ArchetypeEntity>()
               .Where(a => archetypeIds.Contains(a.Id) && a.IsLocked && a.Name.StartsWith("Bot: "))
               .ExecuteDeleteAsync();

        await ctx.UsersToServerRelations
           .Where(x => x.Id == botMember.Id)
           .ExecuteDeleteAsync();

        // Publish lifecycle event to the bot
        await botEventPublisher.PublishBotLifecycleAsync(bot.BotAsUserId,
            BotEventType.BotUninstallingFromSpace, new BotUninstallingFromSpaceEvent(spaceId));

        // Notify bot gateway about space unsubscription
        var gateway = grainFactory.GetGrain<IBotGatewayGrain>(bot.BotAsUserId);
        if (await gateway.IsConnectedAsync())
            await gateway.UnsubscribeFromSpace(spaceId);

        return new UninstallBotGrainResult(true);
    }

    public async Task<ApproveBotEntitlementsGrainResult> ApproveBotEntitlements(Guid botAppId)
    {
        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        // Verify caller is the space owner
        var space = await ctx.Spaces
           .AsNoTracking()
           .Where(s => s.Id == spaceId)
           .Select(s => new { s.CreatorId })
           .FirstOrDefaultAsync();

        if (space is null)
            return new ApproveBotEntitlementsGrainResult(false, ApproveBotEntitlementsError.NOT_FOUND);

        if (space.CreatorId != callerId)
            return new ApproveBotEntitlementsGrainResult(false, ApproveBotEntitlementsError.INSUFFICIENT_PERMISSIONS);

        // Find the bot
        var bot = await ctx.BotEntities
           .AsNoTracking()
           .Where(b => b.AppId == botAppId)
           .Select(b => new { b.BotAsUserId, b.RequiredEntitlements })
           .FirstOrDefaultAsync();

        if (bot is null)
            return new ApproveBotEntitlementsGrainResult(false, ApproveBotEntitlementsError.NOT_FOUND);

        // Find the bot's space member
        var botMember = await ctx.UsersToServerRelations
           .AsNoTracking()
           .Where(x => x.SpaceId == spaceId && x.UserId == bot.BotAsUserId)
           .Select(x => new { x.Id })
           .FirstOrDefaultAsync();

        if (botMember is null)
            return new ApproveBotEntitlementsGrainResult(false, ApproveBotEntitlementsError.NOT_INSTALLED);

        // Find the bot's locked archetype in this space
        var archetype = await ctx.Set<ArchetypeEntity>()
           .Where(a => a.IsLocked && a.SpaceId == spaceId)
           .Join(ctx.Set<SpaceMemberArchetypeEntity>().Where(sma => sma.SpaceMemberId == botMember.Id),
                a => a.Id,
                sma => sma.ArchetypeId,
                (a, _) => a)
           .FirstOrDefaultAsync();

        if (archetype is null)
            return new ApproveBotEntitlementsGrainResult(false, ApproveBotEntitlementsError.NOT_FOUND);

        if (archetype.Entitlement == bot.RequiredEntitlements)
            return new ApproveBotEntitlementsGrainResult(false, ApproveBotEntitlementsError.ALREADY_UP_TO_DATE);

        // Update the locked archetype to match bot's current required entitlements
        archetype.Entitlement = bot.RequiredEntitlements;
        await ctx.SaveChangesAsync();

        // Refetch full entity for DTO mapping
        var fullArchetype = await ctx.Set<ArchetypeEntity>()
           .AsNoTracking()
           .FirstAsync(a => a.Id == archetype.Id);

        // Broadcast archetype update to space clients
        await appHubServer.BroadcastSpace(
            new ArchetypeChanged(spaceId, fullArchetype.ToDto()),
            spaceId);

        return new ApproveBotEntitlementsGrainResult(true);
    }

    // ─── Voice Reverse Index ─────────────────────────────────

    public Task OnUserJoinedVoiceAsync(Guid userId, Guid channelId, DateTimeOffset joinedAt)
    {
        state.State.VoiceMembers[userId] = new VoiceSlot(channelId, joinedAt);
        return state.WriteStateAsync();
    }

    public Task OnUserLeftVoiceAsync(Guid userId)
    {
        if (!state.State.VoiceMembers.Remove(userId))
            return Task.CompletedTask;
        return state.WriteStateAsync();
    }

    public Task<VoiceSlot?> GetUserVoiceSlotAsync(Guid userId)
        => Task.FromResult(state.State.VoiceMembers.GetValueOrDefault(userId));
}