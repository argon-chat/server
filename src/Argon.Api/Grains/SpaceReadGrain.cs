namespace Argon.Grains;

using Argon.Entities;
using Grains.Interfaces;
using ion.runtime;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;
using Services;
using Services.L1L2;

/// <summary>
/// Queries about a space, answered by a pool of activations over a shared cache rather than by the
/// one <see cref="SpaceGrain"/> activation. See <see cref="ISpaceReadGrain"/> for why they moved.
/// </summary>
[StatelessWorker]
public sealed class SpaceReadGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IGrainFactory grainFactory,
    IUserPresenceService userPresence,
    IArchetypeAgent archetypes,
    HybridCache cache) : Grain, ISpaceReadGrain
{
    /// <summary>
    /// Short rather than long. Tag invalidation from the write side is the primary mechanism and this
    /// is only the backstop behind it, but it still has to be long enough that a crowd logging in
    /// together shares one answer instead of each paying for the query.
    /// </summary>
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration           = TimeSpan.FromMinutes(2),
        LocalCacheExpiration = TimeSpan.FromSeconds(30)
    };

    public async Task<SpaceSnapshot> GetSnapshot(SpaceVersions? known)
    {
        var spaceId  = this.GetPrimaryKey();
        var callerId = this.GetUserId();

        var roster = await CachedRosterAsync(spaceId);
        var roles  = await CachedArchetypesAsync(spaceId);

        if (MemberOf(roster.Value, roles.Value, callerId) is not { } member)
            throw new InvalidOperationException($"user '{callerId}' is not a member of space '{spaceId}'");

        var channels = await CachedChannelsAsync(spaceId);
        var groups   = await CachedGroupsAsync(spaceId);

        // The caller's own token, not the space's: which channels they may see depends on their roles
        // as much as on the channel rows, and two members of one space can hold different answers.
        var visibleVersion = SpaceReadVersion.Combine(
            channels.Version, member.Id.ToString(), string.Join(',', member.ArchetypeIds.Order()));

        var versions = new SpaceVersions(roster.Version, visibleVersion, groups.Version, roles.Version);

        // Guarded rather than folded into the call below, because this is the branch that costs
        // something: it fans out to every visible channel for its live occupancy. A caller who is up
        // to date makes no grain calls at all.
        IonArray<RealtimeChannel>? visible = null;

        if (known?.channels != versions.channels)
            visible = new IonArray<RealtimeChannel>(await VisibleChannelsAsync(channels.Value, member));

        return new SpaceSnapshot(versions,
            Unless(known?.members == versions.members, roster.Value),
            visible,
            Unless(known?.groups == versions.groups, groups.Value),
            Unless(known?.archetypes == versions.archetypes, roles.Value));
    }

    /// <summary>
    /// The part, or nothing at all when the caller's token already matched. Null is not an empty
    /// array here: an empty array says the space genuinely has none of that thing.
    /// </summary>
    private static IonArray<T>? Unless<T>(bool matched, List<T> value)
        => matched ? null : (IonArray<T>?)new IonArray<T>(value);

    /// <summary>
    /// Held for a second, which is the difference between a crowd asking once and asking each.
    /// </summary>
    /// <remarks>
    /// Every arriving client asks for the presence of everyone else, so a hundred and fifty arrivals
    /// were a hundred and fifty batch reads of the same hundred and fifty keys inside the same
    /// fraction of a second. One second of staleness costs nothing here: this is the first paint, and
    /// the event stream corrects it immediately after — which is where the client gets every
    /// subsequent change anyway.
    /// </remarks>
    private static readonly HybridCacheEntryOptions PresenceOptions = new()
    {
        Expiration           = TimeSpan.FromSeconds(1),
        LocalCacheExpiration = TimeSpan.FromSeconds(1)
    };

    public async Task<List<MemberPresence>> GetPresence()
    {
        var spaceId = this.GetPrimaryKey();
        var roster  = await CachedRosterAsync(spaceId);

        return await cache.GetOrCreateAsync($"space:presence:{spaceId}",
            (userPresence, roster.Value),
            static async (state, _) =>
            {
                var ids = state.Value.Select(x => x.userId).Distinct().ToList();

                var statuses   = await state.userPresence.BatchGetAggregatedStatusAsync(ids);
                var activities = await state.userPresence.BatchGetUsersActivityPresence(ids);

                return ids.Select(id => new MemberPresence(id,
                    statuses.TryGetValue(id, out var status) ? status : UserStatus.Offline,
                    activities.TryGetValue(id, out var activity) ? activity : null)).ToList();
            },
            PresenceOptions, [ISpaceReadCache.SpaceTag(spaceId)]);
    }

    public async Task<List<RealtimeServerMember>> GetMembers()
    {
        var spaceId = this.GetPrimaryKey();
        var members = await CachedRosterAsync(spaceId);

        // Presence stays outside the cache: it is a batch read out of Redis already, and it is the
        // one part of this answer that genuinely changes second to second.
        var ids        = members.Value.Select(x => x.userId).Distinct().ToList();
        var statuses   = await userPresence.BatchGetAggregatedStatusAsync(ids);
        var activities = await userPresence.BatchGetUsersActivityPresence(ids);

        return members.Value.Select(x => new RealtimeServerMember(
            x,
            statuses.TryGetValue(x.userId, out var s) ? s : UserStatus.Offline,
            activities.TryGetValue(x.userId, out var presence) ? presence : null)).ToList();
    }

    public async Task<List<RealtimeChannel>> GetChannels()
    {
        var spaceId  = this.GetPrimaryKey();
        var callerId = this.GetUserId();

        var roster = await CachedRosterAsync(spaceId);
        var roles  = await CachedArchetypesAsync(spaceId);

        if (MemberOf(roster.Value, roles.Value, callerId) is not { } member)
            throw new InvalidOperationException($"user '{callerId}' is not a member of space '{spaceId}'");

        return await VisibleChannelsAsync((await CachedChannelsAsync(spaceId)).Value, member);
    }

    public async Task<List<ChannelGroup>> GetChannelGroups()
        => (await CachedGroupsAsync(this.GetPrimaryKey())).Value;

    /// <summary>
    /// The channels this member may see, each with its live occupancy.
    /// </summary>
    /// <remarks>
    /// The fan-out is the expensive half and it is per caller, because the visible set is. It stays
    /// out of the cache for the same reason presence does — it is the part that is different a second
    /// later.
    /// </remarks>
    private async Task<List<RealtimeChannel>> VisibleChannelsAsync(List<CachedChannel> channels, CachedMember member)
    {
        // Rebuilt rather than cached: the evaluator reads exactly the two fields these carry, and an
        // EF graph is what the projections exist to keep out of the cache.
        var asMember = member.AsEntity();

        var visible = channels
           .Where(c => EntitlementAnalyzer.IsEntitlementSatisfied(
                EntitlementEvaluator.ApplyPermissionOverwrites(member.BasePermissions, asMember, c.AsEntity()),
                ArgonEntitlement.ViewChannel))
           .ToList();

        var states = await Task.WhenAll(visible
           .Select(x => grainFactory.GetGrain<IChannelGrain>(x.Channel.channelId).GetRealtimeStateAsync()));

        return visible.Zip(states, (channel, state) => new RealtimeChannel(channel.Channel, new(state.Members))).ToList();
    }

    /// <summary>
    /// The roster, which every caller wants in full anyway and which is what a login storm asks for
    /// once per arriving member.
    /// </summary>
    private ValueTask<Versioned<List<SpaceMember>>> CachedRosterAsync(Guid spaceId)
        => cache.GetOrCreateAsync($"space:members:{spaceId}", (context, spaceId),
            static async (state, ct) =>
            {
                await using var db = await state.context.CreateDbContextAsync(ct);

                var rows = await db.UsersToServerRelations
                   .AsNoTracking()
                   .AsSplitQuery()
                   .Include(x => x.User)
                   .Where(x => x.SpaceId == state.spaceId)
                   .Include(x => x.SpaceMemberArchetypes)
                   .ToListAsync(ct);

                return Version(rows.Select(x => x.ToDto()).ToList());
            },
            CacheOptions, [ISpaceReadCache.SpaceTag(spaceId)]);

    private ValueTask<Versioned<List<ChannelGroup>>> CachedGroupsAsync(Guid spaceId)
        => cache.GetOrCreateAsync($"space:groups:{spaceId}", (context, spaceId),
            static async (state, ct) =>
            {
                await using var db = await state.context.CreateDbContextAsync(ct);

                var rows = await db.Set<ChannelGroupEntity>()
                   .AsNoTracking()
                   .Where(g => g.SpaceId == state.spaceId)
                   .OrderBy(g => g.FractionalIndex)
                   .ToListAsync(ct);

                return Version(rows.Select(x => x.ToDto()).ToList());
            },
            CacheOptions, [ISpaceReadCache.SpaceTag(spaceId)]);

    private ValueTask<Versioned<List<CachedChannel>>> CachedChannelsAsync(Guid spaceId)
        => cache.GetOrCreateAsync($"space:channels:{spaceId}", (context, spaceId),
            static async (state, ct) =>
            {
                await using var db = await state.context.CreateDbContextAsync(ct);

                var rows = await db.Channels
                   .AsNoTracking()
                   .AsSplitQuery()
                   .Where(c => c.SpaceId == state.spaceId)
                   .Include(c => c.EntitlementOverwrites)
                   .OrderBy(c => c.ChannelGroupId)
                   .ThenBy(c => c.FractionalIndex)
                   .ToListAsync(ct);

                return Version(rows.Select(c => new CachedChannel(c.ToDto(), c.EntitlementOverwrites
                   .Select(o => new CachedOverwrite(o.Scope, o.ArchetypeId, o.SpaceMemberId, o.Allow, o.Deny))
                   .ToList())).ToList());
            },
            CacheOptions, [ISpaceReadCache.SpaceTag(spaceId)]);

    /// <summary>
    /// The space's archetypes, over the cache that already holds them.
    /// </summary>
    /// <remarks>
    /// A cache over a cache, and on purpose: the inner one hands back a list, and what is wanted here
    /// is the list <em>and</em> its version, computed once rather than on every request. Both are
    /// dropped by the same tag, so they cannot disagree.
    /// </remarks>
    private ValueTask<Versioned<List<Archetype>>> CachedArchetypesAsync(Guid spaceId)
        => cache.GetOrCreateAsync($"space:archetypes:{spaceId}", (archetypes, spaceId),
            static async (state, ct) => Version(await state.archetypes.GetAllAsync(state.spaceId, ct)),
            CacheOptions, [ISpaceReadCache.SpaceTag(spaceId)]);

    private static Versioned<T> Version<T>(T value) => new(SpaceReadVersion.Of(value), value);

    /// <summary>
    /// What the channel filter needs about the caller, assembled from two lists that are already in
    /// hand rather than read back out of the database.
    /// </summary>
    /// <remarks>
    /// This used to be its own cache entry over its own query, keyed by member — which meant one
    /// database round trip per <em>distinct</em> arriving user, so a hundred and fifty people
    /// arriving at once opened a hundred and fifty of them and exhausted the connection pool before
    /// any of them was cached.
    /// <para>
    /// Nothing here needed the database. The roster already carries which archetypes each member
    /// holds, and the archetype list already carries what each archetype grants. Joining them is the
    /// whole computation.
    /// </para>
    /// </remarks>
    private static CachedMember? MemberOf(List<SpaceMember> roster, List<Archetype> granted, Guid userId)
    {
        if (roster.FirstOrDefault(m => m.userId == userId) is not { } member)
            return null;

        var held = member.archetypes.Select(a => a.archetypeId).ToList();

        return new CachedMember(member.memberId,
            granted.Where(a => held.Contains(a.id))
               .Aggregate(ArgonEntitlement.None, (permissions, a) => permissions | a.entitlement),
            held);
    }
}

/// <summary>
/// What the permission filter needs off a member, flattened.
/// </summary>
/// <remarks>
/// The entities cannot go into the cache as EF hands them over: their navigations are cyclic — a
/// member's archetype points at the archetype, whose member list points back at it — and the cache
/// serializer refuses the graph outright. Projecting also keeps the entry down to what is actually
/// read, a few hundred bytes rather than the whole object graph.
/// </remarks>
public sealed record CachedMember(Guid Id, ArgonEntitlement BasePermissions, List<Guid> ArchetypeIds)
{
    public SpaceMemberEntity AsEntity()
        => new()
        {
            Id                    = Id,
            SpaceMemberArchetypes = ArchetypeIds.Select(id => new SpaceMemberArchetypeEntity { ArchetypeId = id }).ToList()
        };
}

public sealed record CachedOverwrite(
    IArchetypeScope Scope,
    Guid? ArchetypeId,
    Guid? SpaceMemberId,
    ArgonEntitlement Allow,
    ArgonEntitlement Deny);

public sealed record CachedChannel(ArgonChannel Channel, List<CachedOverwrite> Overwrites)
{
    public ChannelEntity AsEntity()
        => new()
        {
            EntitlementOverwrites = Overwrites.Select(o => new ChannelEntitlementOverwriteEntity
            {
                Scope         = o.Scope,
                ArchetypeId   = o.ArchetypeId,
                SpaceMemberId = o.SpaceMemberId,
                Allow         = o.Allow,
                Deny          = o.Deny
            }).ToList()
        };
}
