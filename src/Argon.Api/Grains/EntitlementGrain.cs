namespace Argon.Grains;

using Argon.Api.Features.Bus;
using Features.Repositories;
using Services.L1L2;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Drawing;
using Shared;

public class EntitlementGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IServerRepository serverRepository,
    IArchetypeAgent archetypeAgent,
    ILogger<IEntitlementGrain> logger) : Grain, IEntitlementGrain
{
    private IDistributedArgonStream<IArgonEvent> _serverEvents;

    public async override Task OnActivateAsync(CancellationToken ct)
        => _serverEvents = await this.Streams().CreateServerStreamFor(this.GetPrimaryKey());

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken ct)
        => await _serverEvents.DisposeAsync();

    public async Task<List<ArchetypeDto>> GetServerArchetypes()
        => await archetypeAgent.GetAllAsync(this.GetPrimaryKey());

    public async Task<List<ArchetypeDtoGroup>> GetFullyServerArchetypes()
    {
        var             callerId   = this.GetUserId();
        await using var ctx        = await context.CreateDbContextAsync();
        var             archetypes = await archetypeAgent.GetAllAsync(this.GetPrimaryKey());

        if (!await HasAccessAsync(ctx, callerId, ArgonEntitlement.ManageArchetype))
            return archetypes.Select(x => new ArchetypeDtoGroup()
            {
                Archetype = x,
                Members = []
            }).ToList();

        var serverId = this.GetPrimaryKey();

        var members = await ctx.UsersToServerRelations
           .Where(m => m.ServerId == serverId)
           .Include(m => m.ServerMemberArchetypes)
           .ToListAsync();

        var membersWithArchetypes = members.Select(m => new
        {
            MemberId       = m.Id,
            ArchetypeIds = m.ServerMemberArchetypes.Select(sma => sma.ArchetypeId).ToList()
        }).ToList();

        var result = archetypes.Select(a => new ArchetypeDtoGroup
        {
            Archetype = a,
            Members   = []
        }).ToDictionary(g => g.Archetype.Id);

        foreach (var member in membersWithArchetypes)
            foreach (var archId in member.ArchetypeIds)
                if (result.TryGetValue(archId, out var group))
                    group.Members.Add(member.MemberId);

        return result.Values.ToList();
    }

    public async Task<ArchetypeDto> CreateArchetypeAsync(string name)
    {
        var creatorId = this.GetUserId();

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

        Ensure.That(await ctx.SaveChangesAsync() == 1);

        await _serverEvents.Fire(new ArchetypeCreated(arch.ToDto()));

        return await archetypeAgent.DoCreatedAsync(arch);
    }

    public async Task<ArchetypeDto?> UpdateArchetypeAsync(ArchetypeDto dto)
    {
        var callerId = this.GetUserId();

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
                Ensure.That(await ctx.SaveChangesAsync() == 1);
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
        archetype.UpdatedAt     = DateTimeOffset.UtcNow;

        Ensure.That(await ctx.SaveChangesAsync() == 1);
        return await Changed(archetypeEntity.Entity);


        async Task<ArchetypeDto?> Changed(Archetype value)
        {
            var result = value.ToDto();
            await archetypeAgent.DoUpdatedAsync(value);
            await _serverEvents.Fire(new ArchetypeChanged(result));
            return result;
        }
    }

    public async Task<ChannelEntitlementOverwrite?>
        UpsertMemberEntitlementForChannel(Guid channelId, Guid memberId, ArgonEntitlement deny, ArgonEntitlement allow)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();

        if (!await HasAccessAsync(ctx, callerId, ArgonEntitlement.ManageChannels | ArgonEntitlement.ManageArchetype))
            return null;

        var overwrite = await ctx.ChannelEntitlementOverwrites.FirstOrDefaultAsync(x =>
            x.ChannelId == channelId &&
            x.ServerMemberId == memberId
        );

        if (overwrite == null)
        {
            overwrite = new ChannelEntitlementOverwrite
            {
                ChannelId      = channelId,
                ServerMemberId = memberId,
                Allow          = allow,
                Deny           = deny
            };
            ctx.ChannelEntitlementOverwrites.Add(overwrite);
        }
        else
        {
            overwrite.Allow = allow;
            overwrite.Deny  = deny;
            ctx.ChannelEntitlementOverwrites.Update(overwrite);
        }

        Ensure.That(await ctx.SaveChangesAsync() == 1);

            
        return overwrite;
    }

    public async Task<bool> DeleteEntitlementForChannel(Guid channelId, Guid EntitlementOverwriteId)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();

        if (!await HasAccessAsync(ctx, callerId, ArgonEntitlement.ManageChannels | ArgonEntitlement.ManageArchetype))
            return false;

        var overwrite = await ctx.ChannelEntitlementOverwrites.FirstOrDefaultAsync(x =>
            x.ChannelId == channelId &&
            x.Id == EntitlementOverwriteId
        );

        if (overwrite is null) return false;

        ctx.ChannelEntitlementOverwrites.Remove(overwrite);
        Ensure.That(await ctx.SaveChangesAsync() == 1);
        return true;
    }

    public async Task<ChannelEntitlementOverwrite?>
        UpsertArchetypeEntitlementForChannel(Guid channelId, Guid archetypeId, ArgonEntitlement deny, ArgonEntitlement allow)
    {
        await using var ctx = await context.CreateDbContextAsync();

        var callerId = this.GetUserId();

        if (!await HasAccessAsync(ctx, callerId, ArgonEntitlement.ManageChannels | ArgonEntitlement.ManageArchetype))
            return null;

        var overwrite = await ctx.ChannelEntitlementOverwrites.FirstOrDefaultAsync(x =>
            x.ChannelId == channelId &&
            x.ArchetypeId == archetypeId
        );

        if (overwrite == null)
        {
            overwrite = new ChannelEntitlementOverwrite
            {
                ChannelId   = channelId,
                ArchetypeId = archetypeId,
                Allow       = allow,
                Deny        = deny
            };
            ctx.ChannelEntitlementOverwrites.Add(overwrite);
        }
        else
        {
            overwrite.Allow = allow;
            overwrite.Deny  = deny;
            ctx.ChannelEntitlementOverwrites.Update(overwrite);
        }

        Ensure.That(await ctx.SaveChangesAsync() == 1);


        return overwrite;
    }

    public async Task<List<ChannelEntitlementOverwrite>> GetChannelEntitlementOverwrites(Guid channelId)
    {
        await using var ctx = await context.CreateDbContextAsync();
        var e = await ctx.Channels.Include(x => x.EntitlementOverwrites)
           .FirstOrDefaultAsync(x => x.ServerId == this.GetPrimaryKey() && x.Id == channelId);
        return e is null ? [] : e.EntitlementOverwrites.ToList();
    }


    public async Task<bool> SetArchetypeToMember(Guid memberId, Guid archetypeId, bool isGrant)
    {
        var             callerId = this.GetUserId();
        await using var ctx      = await context.CreateDbContextAsync();

        var invoker = await ctx.UsersToServerRelations
           .Where(x => x.ServerId == this.GetPrimaryKey() && x.UserId == callerId)
           .Include(x => x.ServerMemberArchetypes)
           .ThenInclude(x => x.Archetype)
           .FirstOrDefaultAsync();

        if (invoker is null)
            return false;

        var invokerArchetypes = invoker
           .ServerMemberArchetypes
           .Select(x => x.Archetype)
           .ToList();

        if (!invokerArchetypes.Any(x
                => x.Entitlement.HasFlag(ArgonEntitlement.ManageArchetype)))
            return false;

        var targetArchetype = await ctx.Archetypes
           .FirstOrDefaultAsync(x => x.ServerId == this.GetPrimaryKey() && x.Id == archetypeId);

        if (targetArchetype is null)
            return false;

        if (!EntitlementEvaluator.IsAllowedToEdit(targetArchetype, invokerArchetypes))
            return false;

        if (isGrant)
        {
            await ctx.ServerMemberArchetypes.AddAsync(new ServerMemberArchetype()
            {
                ArchetypeId    = archetypeId,
                ServerMemberId = memberId
            });
            Ensure.That(await ctx.SaveChangesAsync() == 1);
            return true;
        }

        var e = await ctx
           .ServerMemberArchetypes
           .FirstOrDefaultAsync(x => x.ServerMemberId == memberId && x.ArchetypeId == archetypeId);

        if (e is null) return false;

        ctx.ServerMemberArchetypes.Remove(e);
        Ensure.That(await ctx.SaveChangesAsync() == 1);
        return true;
    }

    private async Task<bool> HasAccessAsync(ApplicationDbContext ctx, Guid callerId, ArgonEntitlement requiredEntitlement)
    {
        var invoker = await ctx.UsersToServerRelations
           .Where(x => x.ServerId == this.GetPrimaryKey() && x.UserId == callerId)
           .Include(x => x.ServerMemberArchetypes)
           .ThenInclude(x => x.Archetype)
           .FirstOrDefaultAsync();

        if (invoker is null)
            return false;

        var invokerArchetypes = invoker
           .ServerMemberArchetypes
           .Select(x => x.Archetype)
           .ToList();

        return invokerArchetypes.Any(x
            => x.Entitlement.HasFlag(requiredEntitlement));
    }


}