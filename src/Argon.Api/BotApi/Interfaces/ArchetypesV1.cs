namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;

[BotInterface("IArchetypes", 1)]
[BotDescription("List and inspect archetypes (roles) in a space. Permissions are hidden unless the bot has ManageArchetype entitlement.")]
[StableContract("54ffb425be28ff6fa8e95632cda1e1e63919a8b110d2aafb9b6f3c493e24266e")]
[BotRoute("GET", "/List", ResponseType = typeof(ArchetypeListResponse), Description = "Lists all archetypes in a space. Returns id, name, colour, isMentionable, isDefault. Permissions are included only if the bot has the ManageArchetype entitlement.")]
[BotRoute("GET", "/Get",  ResponseType = typeof(BotArchetypeV1), Description = "Gets a single archetype by ID. Pass spaceId and archetypeId as query parameters.")]
[BotRoute("GET", "/ListMembers", ResponseType = typeof(ArchetypeMembersResponse), Description = "Lists all members assigned to a specific archetype. Pass spaceId and archetypeId as query parameters.", Permission = "ViewMembers")]
[BotError("/List", 403, "not_a_member", "Bot is not a member of this space.")]
[BotError("/Get", 403, "not_a_member", "Bot is not a member of this space.")]
[BotError("/Get", 404, "not_found", "Archetype not found.")]
[BotError("/ListMembers", 403, "not_a_member", "Bot is not a member of this space.")]
public sealed class ArchetypesV1(IGrainFactory grains) : IBotInterface
{
    public sealed record ArchetypeListResponse(
        List<BotArchetypeV1> Archetypes);

    public sealed record ArchetypeMemberV1(
        Guid   UserId,
        string Username,
        string DisplayName);

    public sealed record ArchetypeMembersResponse(
        Guid                    ArchetypeId,
        List<ArchetypeMemberV1> Members);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.AddEndpointFilter<BotSpaceMembershipFilter>();
        group.RequireRateLimiting("Bot_IArchetypes");

        group.MapGet("/List", async (HttpContext ctx, Guid spaceId) =>
        {
            var entitlements = grains.GetGrain<IEntitlementGrain>(spaceId);
            var archetypes   = await entitlements.GetServerArchetypes();

            var canViewPerms = await HasManageArchetypeEntitlement(ctx, spaceId);

            return Results.Ok(new ArchetypeListResponse(
                archetypes
                    .Where(a => !a.isHidden)
                    .Select(a => BotEventMapper.FromArchetype(a, canViewPerms))
                    .ToList()));
        });

        group.MapGet("/Get", async (HttpContext ctx, Guid spaceId, Guid archetypeId) =>
        {
            var entitlements = grains.GetGrain<IEntitlementGrain>(spaceId);
            var archetypes   = await entitlements.GetServerArchetypes();
            var archetype    = archetypes.FirstOrDefault(a => a.id == archetypeId);

            if (archetype is null)
                return Results.Json(
                    new BotApiError("not_found", "Archetype not found."),
                    statusCode: StatusCodes.Status404NotFound);

            var canViewPerms = await HasManageArchetypeEntitlement(ctx, spaceId);
            return Results.Ok(BotEventMapper.FromArchetype(archetype, canViewPerms));
        });

        group.MapGet("/ListMembers", async (HttpContext ctx, Guid spaceId, Guid archetypeId) =>
        {
            var entitlements = grains.GetGrain<IEntitlementGrain>(spaceId);
            var groups       = await entitlements.GetFullyServerArchetypes();
            var group2       = groups.FirstOrDefault(g => g.archetype.id == archetypeId);

            if (group2 is null)
                return Results.Json(
                    new BotApiError("not_found", "Archetype not found."),
                    statusCode: StatusCodes.Status404NotFound);

            var space   = grains.GetGrain<ISpaceGrain>(spaceId);
            var members = await space.GetMembers();

            var memberIds = group2.members.Values.ToHashSet();
            var matched = members
                .Where(m => memberIds.Contains(m.member.userId))
                .Select(m => new ArchetypeMemberV1(
                    m.member.userId,
                    m.member.user.username,
                    m.member.user.displayName))
                .ToList();

            return Results.Ok(new ArchetypeMembersResponse(archetypeId, matched));
        });
    }

    private async Task<bool> HasManageArchetypeEntitlement(HttpContext ctx, Guid spaceId)
    {
        try
        {
            var botUserId = ctx.GetBotAsUserId();
            var space     = grains.GetGrain<ISpaceGrain>(spaceId);
            var member    = await space.GetMember(botUserId);

            var entitlementGrain = grains.GetGrain<IEntitlementGrain>(spaceId);
            var archetypes       = await entitlementGrain.GetServerArchetypes();

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
