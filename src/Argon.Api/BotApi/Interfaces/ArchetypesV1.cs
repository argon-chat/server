namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("IArchetypes", 1)]
[BotDescription("List and inspect archetypes (roles) in a space. Permissions are hidden unless the bot has ManageArchetype entitlement.")]
public sealed class ArchetypesV1(IGrainFactory grains) : IBotInterface
{
    public sealed record SpaceQuery(Guid SpaceId);

    public sealed record ArchetypeQuery(Guid SpaceId, Guid ArchetypeId);

    public sealed record ArchetypeListResponse(
        List<BotArchetypeV1> Archetypes);

    public sealed record ArchetypeMemberV1(
        Guid   UserId,
        string Username,
        string DisplayName);

    public sealed record ArchetypeMembersResponse(
        Guid                    ArchetypeId,
        List<ArchetypeMemberV1> Members);

    private static readonly BotError ArchetypeNotFound = new(404, "not_found", "Archetype not found.");

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_IArchetypes");

        group.Get<SpaceQuery, ArchetypeListResponse>("/List")
           .Summary("Lists all archetypes in a space. Returns id, name, colour, isMentionable, isDefault. Permissions are included only if the bot has the ManageArchetype entitlement.")
           .RequiresSpaceMembership()
           .Handle(async (ctx, query) =>
            {
                var archetypes   = await grains.GetGrain<IEntitlementGrain>(query.SpaceId).GetServerArchetypes();
                var canViewPerms = await HasManageArchetypeEntitlement(ctx, query.SpaceId);

                return new ArchetypeListResponse(archetypes
                   .Where(a => !a.isHidden)
                   .Select(a => BotEventMapper.FromArchetype(a, canViewPerms))
                   .ToList());
            });

        group.Get<ArchetypeQuery, BotArchetypeV1>("/Get")
           .Summary("Gets a single archetype by ID.")
           .RequiresSpaceMembership()
           .Throws(ArchetypeNotFound)
           .Handle(async (ctx, query) =>
            {
                var archetypes = await grains.GetGrain<IEntitlementGrain>(query.SpaceId).GetServerArchetypes();
                var archetype  = archetypes.FirstOrDefault(a => a.id == query.ArchetypeId)
                              ?? throw ArchetypeNotFound.Raise();

                return BotEventMapper.FromArchetype(archetype, await HasManageArchetypeEntitlement(ctx, query.SpaceId));
            });

        group.Get<ArchetypeQuery, ArchetypeMembersResponse>("/ListMembers")
           .Summary("Lists all members assigned to a specific archetype.")
           .RequiresSpaceMembership()
           .Throws(ArchetypeNotFound)
           .Handle(async (_, query) =>
            {
                var groups    = await grains.GetGrain<IEntitlementGrain>(query.SpaceId).GetFullyServerArchetypes();
                var archetype = groups.FirstOrDefault(g => g.archetype.id == query.ArchetypeId)
                             ?? throw ArchetypeNotFound.Raise();

                var members   = await grains.GetGrain<ISpaceReadGrain>(query.SpaceId).GetMembers();
                var memberIds = archetype.members.Values.ToHashSet();

                return new ArchetypeMembersResponse(query.ArchetypeId, members
                   .Where(m => memberIds.Contains(m.member.userId))
                   .Select(m => new ArchetypeMemberV1(
                        m.member.userId,
                        m.member.user.username,
                        m.member.user.displayName))
                   .ToList());
            });
    }

    private async Task<bool> HasManageArchetypeEntitlement(HttpContext ctx, Guid spaceId)
    {
        try
        {
            var member     = await grains.GetGrain<ISpaceGrain>(spaceId).GetMember(ctx.GetBotAsUserId());
            var archetypes = await grains.GetGrain<IEntitlementGrain>(spaceId).GetServerArchetypes();

            var memberArchetypeIds = member.member.archetypes.Values
               .Select(a => a.archetypeId)
               .ToHashSet();

            return archetypes
               .Where(a => memberArchetypeIds.Contains(a.id))
               .Any(a => a.entitlement.HasFlag(ArgonEntitlement.ManageArchetype));
        }
        catch
        {
            return false;
        }
    }
}
