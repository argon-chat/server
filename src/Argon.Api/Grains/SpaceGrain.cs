namespace Argon.Grains;

using Argon.Features.Clustering.Regions;

using Argon.Api.Features.Bus;
using Argon.Api.Features.Utils;
using Argon.Api.Grains.Interfaces;
using Argon.Core.Entities.Data;
using Argon.Core.Features.Transport;
using Argon.Features.BotApi;
using Argon.Features.Storage;
using Argon.Features.Moderation;
using Core.Services;
using Features.Logic;
using Features.Repositories;
using ion.runtime;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.GrainDirectory;
using Persistence.States;
using Services.L1L2;
using Instruments;
using System.Linq;

public class SpaceGrain(
    [PersistentState("realtime-server", IUserSessionGrain.StorageId)]
    IPersistentState<RealtimeServerGrainState> state,
    IGrainFactory grainFactory,
    IDbContextFactory<ApplicationDbContext> context,
    IServerRepository serverRepository,
    IUserPresenceService userPresence,
    IArchetypeAgent archetypeAgent,
    IPermissionCache permissionCache,
    IEntitlementChecker entitlementChecker,
    ISystemMessageService systemMessageService,
    AppHubServer appHubServer,
    BotEventPublisher botEventPublisher,
    ISpaceReadCache readCache,
    ILogger<ISpaceGrain> logger) : Grain, ISpaceGrain
{

    private Task Fire<T>(T ev, CancellationToken ct = default) where T : IArgonEvent
        => appHubServer.BroadcastSpace(ev, this.GetPrimaryKey(), ct);

    /// <summary>
    /// Tells <see cref="ISpaceReadGrain"/> that what it has cached about this space is out of date.
    /// Every mutation below that changes the roster, the channels or the groups has to call it —
    /// those answers are served from a different activation, on a different silo more often than not.
    /// </summary>
    private Task Invalidate(CancellationToken ct = default)
        => readCache.SignalInvalidationAsync(this.GetPrimaryKey(), ct);

    public async override Task OnActivateAsync(CancellationToken ct)
    {
        await state.ReadStateAsync(ct);
    }

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
        => await state.WriteStateAsync(ct);

    private const int MaxSpaceNameLength        = 64;
    private const int MaxSpaceDescriptionLength = 1024;

    public async Task<Either<ArgonSpaceBase, ServerCreationError>> CreateSpace(ServerInput input)
    {
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxSpaceNameLength || input.Description is { Length: > MaxSpaceDescriptionLength })
            return ServerCreationError.BAD_MODEL;
        var creatorId = this.GetUserId();

        if (await serverRepository.CreateAsync(this.GetPrimaryKey(), input with { Name = name }, creatorId) is null)
            return ServerCreationError.LIMIT_REACHED;

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
                x.BoostLevel,
                x.IsVerified,
                x.IsOfficial,
                x.HideBoostStrip,
                x.InviteImageFileId,
                x.IsCommunity
            })
           .FirstAsync(s => s.Id == this.GetPrimaryKey());
        return new ArgonSpaceBase(result.Id, result.Name, result.Description!, result.AvatarFileId, result.TopBannedFileId,
            result.BoostCount, result.BoostLevel, result.IsVerified, result.IsOfficial, result.HideBoostStrip, result.InviteImageFileId,
            result.IsCommunity);
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
        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx, spaceId, callerId, ArgonEntitlement.ManageServer);

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage server");

        var name = input.Name?.Trim();
        if (name is { Length: > MaxSpaceNameLength } || input.Description is { Length: > MaxSpaceDescriptionLength })
            throw new ArgumentException("Space name or description is too long");

        var server = await ctx.Spaces
           .FirstAsync(s => s.Id == spaceId);

        server.Name         = string.IsNullOrEmpty(name) ? server.Name : name;
        server.Description  = input.Description ?? server.Description;
        server.AvatarFileId = input.AvatarUrl ?? server.AvatarFileId;

        await ctx.SaveChangesAsync();
        await Invalidate();

        var spaceBase = new ArgonSpaceBase(server.Id, server.Name, server.Description!, server.AvatarFileId, server.TopBannedFileId,
            server.BoostCount, server.BoostLevel, server.IsVerified, server.IsOfficial, server.HideBoostStrip, server.InviteImageFileId,
            server.IsCommunity);
        await Fire(new SpaceDetailsUpdated(spaceId, spaceBase));
        await Fire(new ServerModified(spaceId, IonArray<string>.Empty));
        return server;
    }

    /// <summary>
    /// The activity a member may be shown with, given the status they are being shown with: none,
    /// if that status is Offline.
    /// </summary>
    /// <remarks>
    /// The same rule as <c>SpaceReadGrain.Coherent</c>, which carries the full reasoning for defect
    /// S18 — status and activity are independent Redis reads on clocks five minutes apart, so after
    /// an ungraceful drop the aggregate reads Offline while the activity key is still there, and a
    /// projection that hands both back says "Offline, playing Portal 2", a state the client's own
    /// rules say cannot exist. It is duplicated rather than shared because the two grains sit in
    /// different projections of the same data and neither owns the other; it lives here as well
    /// because <see cref="GetMember"/> is a third door onto the same tuple (the profile card), and
    /// fixing the roster while leaving the card contradicting it is not fixing anything.
    /// </remarks>
    private static UserActivityPresence? Coherent(UserStatus status, UserActivityPresence? activity)
        => status is UserStatus.Offline ? null : activity;

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

        return new RealtimeServerMember(x.ToDto(), status, Coherent(status, presence));
    }

    public async Task SetUserPresence(Guid userId, UserActivityPresence presence)
        => await Fire(new OnUserPresenceActivityChanged(this.GetPrimaryKey(), userId, presence));

    public async Task RemoveUserPresence(Guid userId)
        => await Fire(new OnUserPresenceActivityRemoved(this.GetPrimaryKey(), userId));


    public Task<bool> DoJoinUserAsync(ulong? joinedViaInviteId = null)
        => AddMemberAsync(this.GetUserId(), joinedViaInviteId);

    /// <inheritdoc cref="ISpaceGrain.RemoveMemberAsync"/>
    /// <remarks>
    /// <para>The three halves of a departure, in the order the join path does them in reverse: commit
    /// the row, drop what is cached about the roster, then say so. <see cref="Invalidate"/> is the
    /// signalling variant, so the entry goes on every silo rather than only on this one — a roster is
    /// answered by <c>SpaceReadGrain</c> from whichever activation the caller lands on.</para>
    ///
    /// <para><c>LeavedFromServerUser</c> is the event the product already defines for this and
    /// <c>BotEventPublisher</c> already maps to <c>BotEventType.MemberLeave</c>, so firing it is what
    /// finally gives bots the member-leave their own API promises. Nothing fired it before this
    /// method existed, because there was no leave-space path at all.</para>
    ///
    /// <para>Silent when the row is already gone: the caller is an erasure that may be resumed or
    /// retried (see <c>AccountDeletionGrainState.StepsDone</c>), and a second announcement of the same
    /// departure would be a roster event observers have no way to reconcile.</para>
    /// </remarks>
    public async Task RemoveMemberAsync(Guid userId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var spaceId = this.GetPrimaryKey();

        var removed = await ctx.UsersToServerRelations
           .Where(x => x.SpaceId == spaceId && x.UserId == userId && !x.IsDeleted)
           .ExecuteUpdateAsync(set => set
               .SetProperty(x => x.IsDeleted, true)
               .SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow)
               .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));

        if (removed == 0)
            return;

        await Invalidate();
        await grainFactory.GetGrain<IUserPresenceGrain>(userId).ForgetSpaceAsync(spaceId);
        await Fire(new LeavedFromServerUser(spaceId, userId));
    }

    /// <inheritdoc cref="ISpaceGrain.AnnounceMemberLeftAsync"/>
    /// <remarks>
    /// The last two thirds of <see cref="RemoveMemberAsync"/> and nothing else — deliberately no row
    /// read, not even to check that the membership is really gone. The caller is an erasure replaying
    /// an announcement it knows it owes, and a guard here would refuse exactly the case this exists
    /// for: a membership whose soft-delete committed in the attempt whose announcement threw.
    /// </remarks>
    public async Task AnnounceMemberLeftAsync(Guid userId)
    {
        var spaceId = this.GetPrimaryKey();

        await Invalidate();
        await grainFactory.GetGrain<IUserPresenceGrain>(userId).ForgetSpaceAsync(spaceId);
        await Fire(new LeavedFromServerUser(spaceId, userId));

        logger.LogInformation(
            "Re-announced the departure of user {UserId} from space {SpaceId}", userId, spaceId);
    }

    private async Task<bool> AddMemberAsync(Guid userId, ulong? joinedViaInviteId = null)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var spaceId = this.GetPrimaryKey();

        var exists = await ctx.UsersToServerRelations
           .AnyAsync(x => x.SpaceId == spaceId && x.UserId == userId);

        if (exists)
            return false;

        var member = ArgonId.New();
        ctx.UsersToServerRelations.Add(new SpaceMemberEntity
        {
            Id                = member,
            SpaceId           = spaceId,
            UserId            = userId,
            JoinedViaInviteId = joinedViaInviteId
        });
        await serverRepository.GrantDefaultArchetypeTo(ctx, spaceId, member);
        await ctx.SaveChangesAsync();

        await Invalidate();
        await UserJoined(userId);

        // Detached and best-effort, and it has to stay that way: the membership is committed above and
        // the caller has already been told the join worked, so a system message that cannot be written
        // must not take the join down with it. Same policy as ChannelGrain.FireDetached — Task.Run so
        // the work leaves this activation's turn queue rather than making the next join wait behind a
        // database write nobody is waiting on, one catch, one log line.
        //
        // The log line is the whole point of the wrapper. This used to be a bare `_ =`, which parked
        // every failure in an unobserved task: the join message wrote MessageId 0, the second join
        // into a space hit the duplicate key, and nothing anywhere said so.
        _ = Task.Run(async () =>
        {
            try
            {
                await systemMessageService.SendUserJoinedMessageAsync(spaceId, userId);
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed to write the join message for user {UserId} in space {SpaceId}",
                    userId, spaceId);
            }
        });
        return true;
    }

    public async Task SetBoostStripHidden(bool hidden)
    {
        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        var hasPermission = await entitlementChecker.HasAccessAsync(
            ctx, spaceId, callerId, ArgonEntitlement.ManageServer);

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage server");

        var space = await ctx.Spaces.FirstAsync(x => x.Id == spaceId);
        space.HideBoostStrip = hidden;
        await ctx.SaveChangesAsync();
        await Invalidate();

        var spaceBase = new ArgonSpaceBase(space.Id, space.Name, space.Description!, space.AvatarFileId, space.TopBannedFileId,
            space.BoostCount, space.BoostLevel, space.IsVerified, space.IsOfficial, space.HideBoostStrip, space.InviteImageFileId,
            space.IsCommunity);
        await Fire(new SpaceDetailsUpdated(spaceId, spaceBase));
    }

    public async Task SetPlatformSpaceFlags(bool? isCommunity, bool? isOfficial, CancellationToken ct = default)
    {
        if (isCommunity is null && isOfficial is null)
            return;

        var spaceId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        var space = await ctx.Spaces.FirstAsync(x => x.Id == spaceId, ct);

        if (isCommunity.HasValue)
            space.IsCommunity = isCommunity.Value;
        if (isOfficial.HasValue)
            space.IsOfficial = isOfficial.Value;

        await ctx.SaveChangesAsync(ct);

        // The read cache holds a copy of the space row, so a flip that only pushed the event would
        // be undone by the next snapshot read.
        await Invalidate(ct);

        var spaceBase = new ArgonSpaceBase(space.Id, space.Name, space.Description!, space.AvatarFileId, space.TopBannedFileId,
            space.BoostCount, space.BoostLevel, space.IsVerified, space.IsOfficial, space.HideBoostStrip, space.InviteImageFileId,
            space.IsCommunity);
        await Fire(new SpaceDetailsUpdated(spaceId, spaceBase), ct);
    }

    public async Task<SpaceStats> GetSpaceStats()
    {
        var spaceId = this.GetPrimaryKey();

        // Counted by SpaceReadGrain over its cached roster, instead of reading every member id here.
        var headcount = grainFactory.GetGrain<ISpaceReadGrain>(spaceId).GetHeadcount();

        await using var ctx = await context.CreateDbContextAsync();

        var space = await ctx.Spaces
           .AsNoTracking()
           .Where(x => x.Id == spaceId)
           .Select(x => new { x.BoostCount, x.BoostLevel, x.CreatedAt, ChannelCount = x.Channels.Count() })
           .FirstAsync();

        var counts = await headcount;

        return new SpaceStats(counts.Members, counts.Online, space.ChannelCount, space.BoostCount, space.BoostLevel,
            space.CreatedAt.UtcDateTime);
    }

    /// <summary>
    /// The space half of an invite preview. Short, and dropped with the space read cache; the rename
    /// paths do not signal that, so this bounds how long an old name can show on an invite sheet.
    /// </summary>
    private static readonly HybridCacheEntryOptions InvitePreviewOptions = new()
    {
        Expiration           = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30)
    };

    public async Task<InvitePreview> GetInvitePreview()
    {
        var spaceId   = this.GetPrimaryKey();
        var headcount = grainFactory.GetGrain<ISpaceReadGrain>(spaceId).GetHeadcount();

        // Resolved here rather than injected so the constructor stays as it is.
        var preview = await ServiceProvider.GetRequiredService<HybridCache>().GetOrCreateAsync(
            $"space:invite-preview:{spaceId}", (context, spaceId),
            static async (state, ct) =>
            {
                await using var ctx = await state.context.CreateDbContextAsync(ct);

                var space = await ctx.Spaces
                   .AsNoTracking()
                   .Where(x => x.Id == state.spaceId)
                   .Select(x => new
                    {
                        x.Id, x.Name, x.Description, x.AvatarFileId, x.TopBannedFileId,
                        x.InviteImageFileId, x.IsVerified, x.IsOfficial, x.IsCommunity
                    })
                   .FirstAsync(ct);

                // The room a voice link points at is a property of the invite, not of the space, so it
                // is stitched on by the caller that resolved the code (UserInteractionImpl.PreviewInvite).
                return new InvitePreview(space.Id, space.Name, space.Description ?? "", space.AvatarFileId,
                    space.TopBannedFileId, space.InviteImageFileId, space.IsVerified, space.IsOfficial, 0, 0, null, null,
                    space.IsCommunity);
            },
            InvitePreviewOptions, [ISpaceReadCache.SpaceTag(spaceId)]);

        // The counts stay live: presence is what an invite sheet is most often asked to show.
        var counts = await headcount;

        return preview with { memberCount = counts.Members, onlineCount = counts.Online };
    }

    public Task DoUserUpdatedAsync(ArgonUser user)
        => Fire(new UserUpdated(this.GetPrimaryKey(), user));

    public Task DoUserProfileUpdatedAsync(Guid userId, ArgonUserProfile profile)
        => Fire(new UserProfileUpdated(this.GetPrimaryKey(), userId, profile));

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
        // Guests never registered, so there is no "in Argon since" to show for them.
        if (IsGuestUserId(userId))
            return PlaceholderProfile(userId, "Guest User");

        var spaceId = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync();

        // The membership in this space, filters on, as PrefetchProfiles reads it: the roles on the card
        // are the ones held here, and a member who left is answered with the placeholder.
        var targetMember = await ctx.UsersToServerRelations
           .AsNoTracking()
           .Where(member => member.SpaceId == spaceId && member.UserId == userId)
           .Include(member => member.User)
           .ThenInclude(user => user.Profile)
           .Include(member => member.SpaceMemberArchetypes)
           .FirstOrDefaultAsync();

        if (targetMember?.User?.Profile is not { } profile)
            return PlaceholderProfile(userId, "Deleted Account");

        var worn = await GrainFactory.GetGrain<ICosmeticsReadGrain>(Guid.Empty).GetWornAsync([userId]);

        return profile.ToDto() with
        {
            archetypes = new(targetMember.SpaceMemberArchetypes.Select(x => x.ToDto())),
            cosmetics  = worn[userId]
        };
    }

    /// <summary>
    /// Everyone a member list is showing, in one query instead of one round trip each.
    /// </summary>
    /// <remarks>
    /// <para>One entry per requested id, in the order asked — a caller pairs the two up by position
    /// or by <c>userId</c>, whichever it finds easier — with the same placeholders
    /// <see cref="PrefetchProfile"/> uses for an id there is nothing to say about.</para>
    /// <para>Scoped to this space, because the archetypes on a profile are the roles the member holds
    /// <em>here</em> and a member of several spaces holds a different set in each; and it answers only
    /// about current members, because a departure is a soft delete and a membership that has ended is
    /// not a membership. Stricter than its single-member counterpart in one way: the caller has to be
    /// a member too.</para>
    /// </remarks>
    public async Task<List<ArgonUserProfile>> PrefetchProfiles(List<Guid> userIds)
    {
        // A member list asks about what it is showing and chunks anything larger, so this is a
        // ceiling rather than a working size. It is a ceiling at all because the ids come from the
        // caller: without one, a single call walks the profile of every account it can name.
        const int maxBatch = 100;

        var asked = userIds.Count > maxBatch ? userIds.Take(maxBatch).ToList() : userIds;

        // Guests never registered, so there is nothing to look up for them — and the rest is one
        // query however many ids came in.
        var lookup = asked.Where(id => !IsGuestUserId(id)).Distinct().ToList();
        var found  = new Dictionary<Guid, ArgonUserProfile>(lookup.Count);

        if (lookup.Count > 0)
        {
            var spaceId  = this.GetPrimaryKey();
            var callerId = this.GetUserId();

            await using var ctx = await context.CreateDbContextAsync();

            // The space id is what says the caller has met these people at all — a hundred ids and
            // no membership behind them is a directory walk, not a member list.
            var callerIsMember = await ctx.UsersToServerRelations
               .AsNoTracking()
               .AnyAsync(member => member.SpaceId == spaceId && member.UserId == callerId);

            if (!callerIsMember)
                throw new InvalidOperationException($"user '{callerId}' is not a member of space '{spaceId}'");

            // Soft-delete filters left on, on both queries: a departure is a soft delete
            // (RemoveMemberAsync writes exactly that), so a membership that has ended neither opens
            // the door for the caller nor answers for the member. Former members fall through to
            // the placeholder below, taking the roles they used to hold with them.
            var members = await ctx.UsersToServerRelations
               .AsNoTracking()
               .Where(member => member.SpaceId == spaceId && lookup.Contains(member.UserId))
               .Include(member => member.User)
               .ThenInclude(user => user.Profile)
               .Include(member => member.SpaceMemberArchetypes)
               .ToListAsync();

            foreach (var member in members)
            {
                // A member row with no profile row behind it is not worth failing the other
                // ninety-nine over; it reads as an account that is no longer there.
                if (member.User?.Profile is not { } profile)
                    continue;

                found[member.UserId] = profile.ToDto() with
                {
                    archetypes = new(member.SpaceMemberArchetypes.Select(x => x.ToDto()))
                };
            }

            // What they are wearing, for the members found and nobody else: the membership checks
            // above are the only thing standing between a caller and anybody's profile, so the
            // cosmetics ride on them rather than being asked for by id on their own.
            if (found.Count > 0)
            {
                var worn = await GrainFactory.GetGrain<ICosmeticsReadGrain>(Guid.Empty).GetWornAsync(found.Keys.ToList());

                foreach (var (memberId, wornBy) in worn)
                {
                    found[memberId] = found[memberId] with { cosmetics = wornBy };
                }
            }
        }

        return asked
           .Select(id => IsGuestUserId(id)
                ? PlaceholderProfile(id, "Guest User")
                : found.GetValueOrDefault(id) ?? PlaceholderProfile(id, "Deleted Account"))
           .ToList();
    }

    /// <summary>
    /// What a profile looks like when there is no profile: the id, the one line explaining why, and
    /// nothing else. Guests never registered, so there is no "in Argon since" to show for them.
    /// </summary>
    private static ArgonUserProfile PlaceholderProfile(Guid userId, string bio)
        => new(userId, null, null, null, null, bio, IonArray<string>.Empty,
            IonArray<SpaceMemberArchetype>.Empty, null, null, null, null, null, null, null,
            IonArray<IWornCosmetic>.Empty);

    public async Task<ArgonUser> PrefetchUser(Guid userId, CancellationToken ct = default)
    {
        if (IsGuestUserId(userId))
            return new ArgonUser(userId, "guest", "Guest User", null, UserFlag.NONE);

        await using var ctx = await context.CreateDbContextAsync(ct);
        
        // Only IsVerified is needed from the bot; projecting it avoids materializing the
        // whole TPT BotEntity (DevApps + Bots) for every prefetched user.
        var row = await ctx.Users
           .IgnoreQueryFilters()
           .AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => new { User = u, IsVerified = u.BotEntity != null && u.BotEntity.IsVerified })
           .FirstOrDefaultAsync(ct);

        if (row is null)
            return new ArgonUser(userId, "unknown", "Unknown User", null, UserFlag.NONE);

        return UserEntity.Map(row.User, row.IsVerified);
    }


    /// <summary>
    /// Tells the space a member arrived, and seeds the new audience with that member's real status —
    /// nothing at all when they are offline.
    /// </summary>
    /// <remarks>
    /// <para>Defect S4, pinned by
    /// <c>PresenceRealtimeTests.A_joiner_with_no_connection_is_not_announced_online</c>,
    /// <c>PresenceRealtimeTests.A_dnd_member_joining_a_space_is_announced_as_dnd_and_a_later_heartbeat_repairs_it</c>
    /// and <c>PresenceVoiceAndCountsTests.A_member_who_never_connected_is_neither_counted_nor_announced</c>.
    /// This used to be a flat <c>SetUserStatus(userId, UserStatus.Online)</c>. It runs on every
    /// membership creation — invite accept, space creation, bot install — and the joiner is not
    /// necessarily the actor and not necessarily connected at all, so the space was told a status
    /// its owner had never asserted: a Do-Not-Disturb user was announced as available to the room
    /// they had just entered, and someone who accepted an invite without ever opening the app was
    /// announced Online for good. It also contradicted this very grain in the same second, since
    /// <see cref="GetMember"/>, <c>GetSpaceStats</c> and the <c>SpaceReadGrain</c> snapshot all read
    /// the aggregate — header and roster disagreed permanently.</para>
    ///
    /// <para>Nothing corrected it afterwards. A heartbeat re-asserting the same status is a no-op in
    /// <c>UserSessionGrain.HeartBeatAsync</c>, and even a forced one is swallowed by
    /// <c>MarkBroadcastIfChangedAsync</c>, whose record still holds the status the user really has.
    /// A join is not a transition — it is a seed for one new audience — so the announcement it needs
    /// is one the per-user hysteresis must not be allowed to suppress.</para>
    ///
    /// <para>Offline is announced as silence rather than as <c>UserChangedStatus(Offline)</c>: the
    /// space has never heard of this member, so there is no stale value to correct, and an explicit
    /// Offline would be one more event for every member of the space to process on every join.</para>
    ///
    /// <para><b>Offline is not always offline.</b> Since the S3 change, a hub attach takes the "alive"
    /// half only — <c>UserSessionGrain.EnsureSessionStartedAsync(null)</c> writes the presence key
    /// but no <c>status:user:{u}:session:{sid}</c>, and therefore no aggregate — so a genuinely
    /// connected client reads Offline for the few seconds between attaching and its first heartbeat
    /// (or the grain's status deadline). A user who cold-starts and immediately accepts an invite or
    /// creates a space lands exactly in that window, and there is no honest status to announce for
    /// them: <c>Online</c> would be the S4 phantom all over again for a DND user, and it is not this
    /// grain's business to guess.</para>
    ///
    /// <para><b>Why this delegates rather than reading the aggregate itself.</b> It used to do both —
    /// read <c>status:user:{u}:aggregated</c>, announce it to this space, and, when it read Offline
    /// under a live session, write <c>lastbroadcast = Offline</c> so the first real status would count
    /// as a change. That last write raced the thing it was there to enable: <c>UserGrain</c> is a
    /// <c>[StatelessWorker]</c>, the joiner's first heartbeat runs the ordinary fan-out on another
    /// activation in the same second, and the two orders interleave — heartbeat writes the aggregate
    /// Online and fans out, then this write lands and leaves the record claiming Offline for an Online
    /// user. The record and the aggregate then disagree in the direction that suppresses the
    /// <em>next</em> transition for every space the user is in. Ordering it by hand is not available
    /// here; giving one owner the read, the record and the announcement is, so the seed is passed to
    /// <see cref="IUserPresenceGrain.AggregateAndBroadcastStatusAsync(Guid[],CancellationToken)"/> —
    /// one activation per user, turn by turn, which is where that ownership now actually lives — and
    /// this grain writes no presence state at all. That grain publishes to space groups directly
    /// rather than calling back into <c>ISpaceGrain</c>, which is what lets this await it from inside
    /// this very turn without deadlocking on ourselves. The membership is committed before this runs (<see cref="AddMemberAsync"/> saves,
    /// <c>IServerRepository.CreateAsync</c> commits its transaction), which is what lets that call
    /// treat the seed as a space the user is already in.</para>
    ///
    /// <para>What is left open, stated plainly: a session that is alive but statusless at the moment
    /// of the join is announced nothing, and if its first status happens to equal the one the
    /// hysteresis record still holds from an earlier connection, the fan-out that would have carried
    /// it here is suppressed — so the new space shows the member grey until an observer's client
    /// reloads <c>GetMemberPresence</c>. The window is the statusless one the status deadline bounds
    /// to seconds, and the cure for the rest of it belongs to the hysteresis record's own lifetime
    /// rather than to a write from here.</para>
    /// </remarks>
    public async ValueTask UserJoined(Guid userId)
    {
        await Fire(new JoinToServerUser(this.GetPrimaryKey(), userId));

        await GrainFactory.GetGrain<IUserPresenceGrain>(userId)
           .AggregateAndBroadcastStatusAsync([this.GetPrimaryKey()]);
    }

    /// <summary>
    /// Announces a member's status to this space, unconditionally.
    /// </summary>
    /// <remarks>
    /// Deliberately without a per-space "what were they last told" comparison. Its other callers —
    /// the <c>UserGrain</c> fan-out, which has already passed the per-user hysteresis, and
    /// <c>BotGatewayGrain</c>, whose ticks re-assert Online precisely to repair a space activation
    /// that lost its push state — rely on it firing every time they ask. The duplicate-suppression
    /// that belongs to the join path lives in <see cref="UserJoined"/> and at the bot-install call
    /// site instead.
    /// </remarks>
    public async Task SetUserStatus(Guid userId, UserStatus status)
    {
        await Fire(new UserChangedStatus(this.GetPrimaryKey(), userId, status, new IonArray<string>([""])));
    }

    /// <summary>
    /// Removes the space itself; the schema cascades the rest.
    /// </summary>
    /// <remarks>
    /// <para>Channels, groups, memberships, archetypes and invites all hang off a required
    /// <c>SpaceId</c>, and a required relationship cascades by default in EF, so deleting them here
    /// by hand would only be a second, less reliable copy of what the database already guarantees.</para>
    ///
    /// <para>Not callable from the client. <see cref="ISpaceDeletionGrain"/> owns the schedule and
    /// the permission check, and this runs only once its grace period is over.</para>
    /// </remarks>
    public async Task DeleteSpace()
    {
        await using var ctx = await context.CreateDbContextAsync();

        var space = await ctx.Spaces.FirstOrDefaultAsync(x => x.Id == this.GetPrimaryKey());

        if (space is null)
            return;

        ctx.Spaces.Remove(space);
        await ctx.SaveChangesAsync();
        await Invalidate();
    }

    public async Task AnnounceDeletionScheduled(SpaceDeletionState deletionState)
        => await Fire(new SpaceDeletionScheduled(this.GetPrimaryKey(), deletionState));

    public async Task AnnounceDeletionCancelled()
        => await Fire(new SpaceDeletionCancelled(this.GetPrimaryKey()));

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

        name = RequireName(name);
        RequireDescription(description, MaxGroupDescriptionLength);

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

        await Invalidate();
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

        if (name is not null)
            name = RequireName(name);
        RequireDescription(description, MaxGroupDescriptionLength);

        var group = await ctx.Set<ChannelGroupEntity>()
           .FirstOrDefaultAsync(g => g.Id == groupId && g.SpaceId == spaceId, cancellationToken: ct);

        if (group == null)
            throw new InvalidOperationException("Channel group not found");

        group.Name        = name ?? group.Name;
        group.Description = description ?? group.Description;
        if (isCollapsed.HasValue)
            group.IsCollapsed = isCollapsed.Value;

        await ctx.SaveChangesAsync(ct);

        await Invalidate(ct);
        await Fire(new ChannelGroupModified(spaceId, group.Id, group.ToDto()), ct);

        return group;
    }

    private const int RebalanceThreshold = 20;

    private const int MaxNameLength                = 128;
    private const int MaxChannelDescriptionLength  = 1024;
    private const int MaxGroupDescriptionLength    = 512;

    /// <summary>The rule <c>ChannelGrain.UpdateChannelSettings</c> applies to a rename.</summary>
    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength)
            throw new ArgumentException($"Name must be 1 to {MaxNameLength} characters");

        return trimmed;
    }

    private static void RequireDescription(string? description, int maxLength)
    {
        if (description is { } text && text.Length > maxLength)
            throw new ArgumentException($"Description must be at most {maxLength} characters");
    }

    /// <summary>The foreign key accepts any group row, including another space's.</summary>
    private static async Task RequireGroupInSpace(ApplicationDbContext ctx, Guid spaceId, Guid? groupId)
    {
        if (groupId is { } id && !await ctx.Set<ChannelGroupEntity>().AnyAsync(g => g.Id == id && g.SpaceId == spaceId))
            throw new ArgumentException("Channel group not found");
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
            else if (afterIndex == null && beforeIndex is { } top)
            {
                // Not Before(top): it decrements and refuses to land on the minimum.
                newIndex = FractionalIndex.Between(FractionalIndex.Min(), top);
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

        await Invalidate();
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

        await Invalidate();
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

        var name = RequireName(input.Name);
        RequireDescription(input.Description, MaxChannelDescriptionLength);

        if (!Enum.IsDefined(input.ChannelType))
            throw new ArgumentException($"Unknown channel type {input.ChannelType}");

        await RequireGroupInSpace(ctx, spaceId, groupId);

        var lastChannel = await ctx.Set<ChannelEntity>()
           .Where(c => c.SpaceId == spaceId && c.ChannelGroupId == groupId)
           .OrderByDescending(c => c.FractionalIndex)
           .FirstOrDefaultAsync();

        var fractionalIndex = lastChannel != null && !string.IsNullOrEmpty(lastChannel.FractionalIndex)
            ? FractionalIndex.After(FractionalIndex.Parse(lastChannel.FractionalIndex))
            : FractionalIndex.Min();

        var channel = new ChannelEntity
        {
            // The space's region, not this process's. Space metadata is replicated everywhere, so
            // this activation can be anywhere — but the channel's messages live where the space
            // lives, and the id is what says so. Explicit at all because it used to be left to EF's
            // value generator, and because the hub's typing pair holds a channel id with no space
            // beside it.
            Id              = ArgonId.NewIn(spaceId),
            Name            = name,
            CreatorId       = callerId,
            Description     = input.Description,
            ChannelType     = input.ChannelType,
            SpaceId         = spaceId,
            ChannelGroupId  = groupId,
            FractionalIndex = fractionalIndex.Value
        };

        await ctx.Set<ChannelEntity>().AddAsync(channel);
        await ctx.SaveChangesAsync();
        await Invalidate();
        await Fire(new ChannelCreated(spaceId, channel.ToDto()));
        return channel;
    }

    public async Task MoveChannel(Guid channelId, Guid? targetGroupId, Guid? afterChannelId, Guid? beforeChannelId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        var hasPermission = await entitlementChecker.HasChannelAccessAsync(spaceId, channelId, callerId, ArgonEntitlement.ManageChannels);

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage channels");

        var channel = await ctx.Set<ChannelEntity>().FindAsync(channelId);
        if (channel == null || channel.SpaceId != spaceId)
            return;

        await RequireGroupInSpace(ctx, spaceId, targetGroupId);

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
            else if (afterIndex == null && beforeIndex is { } top)
            {
                // Not Before(top): it decrements and refuses to land on the minimum.
                newIndex = FractionalIndex.Between(FractionalIndex.Min(), top);
            }
            else
            {
                newIndex = FractionalIndex.Between(afterIndex, beforeIndex);
            }
        }

        channel.FractionalIndex = newIndex.Value;

        if (channel.FractionalIndex.Length > RebalanceThreshold)
            await RebalanceChannelOrder(ctx, spaceId, channel);

        await ctx.SaveChangesAsync();

        await Invalidate();
        await Fire(new ChannelReordered(spaceId, channelId, targetGroupId, channel.FractionalIndex));
    }

    public async Task DeleteChannel(Guid channelId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        var hasPermission = await entitlementChecker.HasChannelAccessAsync(spaceId, channelId, callerId, ArgonEntitlement.ManageChannels);

        if (!hasPermission)
            throw new UnauthorizedAccessException("No permission to manage channels");

        var channel = await ctx.Set<ChannelEntity>().FindAsync(channelId);
        if (channel == null || channel.SpaceId != spaceId)
            return;

        ctx.Set<ChannelEntity>().Remove(channel);
        await ctx.SaveChangesAsync();
        await Invalidate();
        await Fire(new ChannelRemoved(spaceId, channelId));
    }

    public async Task<Either<ArgonChannel, DuplicateChannelError>> DuplicateChannel(Guid channelId, CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(spaceId, channelId, callerId, ArgonEntitlement.ManageChannels, ct))
            return DuplicateChannelError.INSUFFICIENT_PERMISSIONS;

        var source = await ctx.Set<ChannelEntity>()
           .Include(c => c.EntitlementOverwrites)
           .FirstOrDefaultAsync(c => c.Id == channelId && c.SpaceId == spaceId, ct);
        if (source is null)
            return DuplicateChannelError.CHANNEL_NOT_FOUND;

        // Right after the original, so the copy lands where the eye expects it rather than at the
        // bottom of the group. A group holds a handful of channels, so ordering them in memory is
        // cheaper than expressing "the next one" in SQL.
        var siblings = await ctx.Set<ChannelEntity>()
           .Where(c => c.SpaceId == spaceId && c.ChannelGroupId == source.ChannelGroupId)
           .OrderBy(c => c.FractionalIndex)
           .Select(c => new { c.Id, c.FractionalIndex })
           .ToListAsync(ct);

        var position    = siblings.FindIndex(c => c.Id == source.Id);
        var sourceIndex = ParseIndex(source.FractionalIndex);
        var nextIndex   = position >= 0 && position + 1 < siblings.Count ? ParseIndex(siblings[position + 1].FractionalIndex) : null;

        FractionalIndex fractionalIndex;
        try
        {
            fractionalIndex = FractionalIndex.Between(sourceIndex, nextIndex);
        }
        catch (InvalidOperationException)
        {
            // No room left between the two: fall back to the end of the group rather than fail.
            var last = ParseIndex(siblings.LastOrDefault()?.FractionalIndex);
            fractionalIndex = last is { } l ? FractionalIndex.After(l) : FractionalIndex.Min();
        }

        var copy = new ChannelEntity
        {
            Id                    = ArgonId.NewIn(spaceId),
            Name                  = source.Name,
            CreatorId             = callerId,
            Description           = source.Description,
            ChannelType           = source.ChannelType,
            SpaceId               = spaceId,
            ChannelGroupId        = source.ChannelGroupId,
            FractionalIndex       = fractionalIndex.Value,
            SlowMode              = source.SlowMode,
            Bitrate               = source.Bitrate,
            DoNotRestrictBoosters = source.DoNotRestrictBoosters,
        };

        // The overwrites are what make a duplicate worth having over "add channel": a private room
        // copied without them would be public until somebody noticed.
        foreach (var o in source.EntitlementOverwrites)
        {
            copy.EntitlementOverwrites.Add(new ChannelEntitlementOverwriteEntity
            {
                ChannelId     = copy.Id,
                Scope         = o.Scope,
                ArchetypeId   = o.ArchetypeId,
                SpaceMemberId = o.SpaceMemberId,
                Allow         = o.Allow,
                Deny          = o.Deny,
                CreatorId     = callerId,
            });
        }

        await ctx.Set<ChannelEntity>().AddAsync(copy, ct);
        await ctx.SaveChangesAsync(ct);
        await Invalidate();

        // The DTO, not the entity: the entity drags its overwrites along, and they do not survive the copy.
        var dto = copy.ToDto();
        await Fire(new ChannelCreated(spaceId, dto), ct);
        return dto;

        static FractionalIndex? ParseIndex(string? value)
            => string.IsNullOrEmpty(value) ? null : FractionalIndex.Parse(value);
    }

    private static FilePurpose MapPurpose(SpaceFileKind kind)
        => kind switch
        {
            SpaceFileKind.Avatar        => FilePurpose.SpaceAvatar,
            SpaceFileKind.ProfileHeader => FilePurpose.Banner,
            SpaceFileKind.InviteImage   => FilePurpose.InviteImage,
            _                           => FilePurpose.SpaceAvatar
        };

    public async ValueTask<Either<UploadTicket, UploadFileError>> BeginUploadSpaceFile(SpaceFileKind kind, CancellationToken ct = default)
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

        // The fullscreen invite splash image is a premium surface — only verified/official spaces may set it.
        if (kind == SpaceFileKind.InviteImage)
        {
            var badges = await ctx.Spaces
               .AsNoTracking()
               .Where(x => x.Id == spaceId)
               .Select(x => new { x.IsVerified, x.IsOfficial })
               .FirstOrDefaultAsync(ct);

            if (badges is null || (!badges.IsVerified && !badges.IsOfficial))
                return UploadFileError.NOT_AUTHORIZED;
        }

        try
        {
            var fileGrain = GrainFactory.GetGrain<IFileStorageGrain>(callerId);
            var response = await fileGrain.RequestUploadAsync(
                new FileUploadRequest(MapPurpose(kind), "image/", 0, spaceId, null), ct);
            return new UploadTicket(response.BlobId, response.Url, response.Fields, response.TtlSeconds);
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

        var fileGrain = GrainFactory.GetGrain<IFileStorageGrain>(callerId);
        var fileInfo = await fileGrain.FinalizeUploadAsync(blobId, ct);

        if (kind == SpaceFileKind.Avatar)
        {
            var modGrain = GrainFactory.GetGrain<IContentModerationGrain>(Guid.Empty);
            var modResult = await modGrain.EvaluateAsync(fileInfo.S3Key, FilePurpose.SpaceAvatar, ct);

            if (modResult.Action == ContentAction.Deny)
            {
                await fileGrain.DecrementRefAsync(fileInfo.FileId, ct);

                await RecordViolationAsync(callerId, fileInfo.FileId, FilePurpose.SpaceAvatar, modResult, ct);

                logger.LogWarning(
                    "Space avatar rejected for space {SpaceId} by user {UserId}, file {FileId}, stages={Stages}, scores={Scores}, refined={RefinedScores}",
                    spaceId, callerId, fileInfo.FileId, modResult.StagesUsed,
                    FormatScores(modResult.Scores), FormatScores(modResult.RefinedScores));

                throw new ContentViolationException("Space avatar rejected by content moderation");
            }
        }

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
            case SpaceFileKind.InviteImage:
                space.InviteImageFileId = fileId.ToString();
                break;
        }

        await ctx.SaveChangesAsync(ct);
        await Invalidate(ct);

        var spaceBase = new ArgonSpaceBase(space.Id, space.Name, space.Description!, space.AvatarFileId, space.TopBannedFileId,
            space.BoostCount, space.BoostLevel, space.IsVerified, space.IsOfficial, space.HideBoostStrip, space.InviteImageFileId,
            space.IsCommunity);
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

    private static async Task RebalanceChannelOrder(ApplicationDbContext ctx, Guid spaceId, ChannelEntity moved)
    {
        // By id as well: a channel moved in from another group is still filed under that one in the database.
        var items = await ctx.Set<ChannelEntity>()
           .Where(c => c.SpaceId == spaceId && (c.ChannelGroupId == moved.ChannelGroupId || c.Id == moved.Id))
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
            Id          = ArgonId.New(),
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
        await Invalidate();

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

        await Invalidate();
        await grainFactory.GetGrain<IUserPresenceGrain>(bot.BotAsUserId).ForgetSpaceAsync(spaceId);
        await Fire(new LeavedFromServerUser(spaceId, bot.BotAsUserId));

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

        await archetypeAgent.DoUpdatedAsync(archetype);
        await permissionCache.SignalSpaceInvalidationAsync(spaceId);
        await Invalidate();

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

    public Task OnUserLeftVoiceAsync(Guid userId, Guid channelId)
    {
        // A late leave from the previous room must not erase the slot of the room the user is in now.
        if (!state.State.VoiceMembers.TryGetValue(userId, out var slot) || slot.ChannelId != channelId)
            return Task.CompletedTask;

        state.State.VoiceMembers.Remove(userId);
        return state.WriteStateAsync();
    }

    public async Task<IVoiceModerationResult> SetMemberVoiceModeration(Guid memberId, bool? muted, bool? deafened,
        CancellationToken ct = default)
    {
        var callerId = this.GetUserId();
        var spaceId  = this.GetPrimaryKey();

        await using var ctx = await context.CreateDbContextAsync(ct);

        if (muted.HasValue && !await entitlementChecker.HasAccessAsync(ctx, spaceId, callerId, ArgonEntitlement.MuteMember, ct))
            return new FailedVoiceModeration(VoiceModerationError.INSUFFICIENT_PERMISSIONS);
        if (deafened.HasValue && !await entitlementChecker.HasAccessAsync(ctx, spaceId, callerId, ArgonEntitlement.DeafenMember, ct))
            return new FailedVoiceModeration(VoiceModerationError.INSUFFICIENT_PERMISSIONS);

        var member = await ctx.UsersToServerRelations
           .FirstOrDefaultAsync(m => m.SpaceId == spaceId && m.UserId == memberId && !m.IsDeleted, ct);
        if (member is null)
            return new FailedVoiceModeration(VoiceModerationError.MEMBER_NOT_FOUND);

        var ownerId = await ctx.Spaces.Where(s => s.Id == spaceId).Select(s => s.CreatorId).FirstAsync(ct);
        if (memberId == ownerId && callerId != ownerId)
            return new FailedVoiceModeration(VoiceModerationError.CANNOT_MODERATE_OWNER);

        var nextMuted    = muted ?? member.IsVoiceMuted;
        var nextDeafened = deafened ?? member.IsVoiceDeafened;

        if (nextMuted != member.IsVoiceMuted || nextDeafened != member.IsVoiceDeafened)
        {
            member.IsVoiceMuted    = nextMuted;
            member.IsVoiceDeafened = nextDeafened;
            await ctx.SaveChangesAsync(ct);

            ChannelGrainInstrument.VoiceModerations.Add(1,
                new KeyValuePair<string, object?>("muted", nextMuted),
                new KeyValuePair<string, object?>("deafened", nextDeafened));

            if (state.State.VoiceMembers.TryGetValue(memberId, out var slot))
                await grainFactory.GetGrain<IChannelGrain>(slot.ChannelId).ApplyVoiceRestriction(memberId, nextMuted, nextDeafened);
        }

        return new SuccessVoiceModeration(nextMuted, nextDeafened);
    }

    public Task<VoiceSlot?> GetUserVoiceSlotAsync(Guid userId)
        => Task.FromResult(state.State.VoiceMembers.GetValueOrDefault(userId));

    // ─── Content Moderation ──────────────────────────────────

    private async ValueTask RecordViolationAsync(
        Guid userId, Guid fileId, FilePurpose purpose,
        ContentModerationResult result, CancellationToken ct)
    {
        try
        {
            await using var ctx = await context.CreateDbContextAsync(ct);
            ctx.ContentViolations.Add(new ContentViolationEntity
            {
                Id = ArgonId.New(),
                UserId = userId,
                FileId = fileId,
                FilePurpose = purpose,
                StagesUsed = result.StagesUsed,
                PrimaryScores = result.Scores,
                RefinedScores = result.RefinedScores,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await ctx.SaveChangesAsync(ct);

            ModerationInstruments.ViolationsRecorded.Add(1,
                new KeyValuePair<string, object?>("purpose", purpose.ToString()));
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to record content violation for user {UserId}", userId);
        }
    }

    private static string FormatScores(Dictionary<string, float>? scores)
        => scores is null or { Count: 0 }
            ? "-"
            : string.Join(", ", scores.Select(kv => $"{kv.Key}={kv.Value:P1}"));
}