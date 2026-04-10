namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;

[BotInterface("ISpaces", 1)]
[BotDescription("Get space details and member information.")]
[StableContract("4e98c00f57070e7d70cce6ccbac2c6488f62cc11c1fcf7623bfeb327d9a1adc6")]
[BotRoute("GET", "/Get",         ResponseType = typeof(BotSpaceDetail), Description = "Gets space details — name, description, and community flag. Pass spaceId as a query parameter.")]
[BotRoute("GET", "/ListMembers", ResponseType = typeof(MemberListResponse), Description = "Lists all members of a space with their username, display name, and roles. Pass spaceId as a query parameter. Requires privileged intent.", Permission = "ViewMembers", IsPrivileged = true)]
[BotRoute("GET", "/GetMember",   ResponseType = typeof(BotMember), Description = "Gets a single member's details. Pass spaceId and userId as query parameters.", Permission = "ViewMembers")]
[BotError("/Get", 403, "not_a_member", "Bot is not a member of this space.")]
[BotError("/ListMembers", 403, "not_a_member", "Bot is not a member of this space.")]
[BotError("/GetMember", 403, "not_a_member", "Bot is not a member of this space.")]
public sealed class SpacesV1(IGrainFactory grains) : IBotInterface
{
    public sealed record BotSpaceDetail(
        Guid    SpaceId,
        string  Name,
        string? Description,
        bool    IsCommunity);

    public sealed record BotMember(
        Guid       UserId,
        Guid       SpaceId,
        string     Username,
        string     DisplayName,
        List<Guid> ArchetypeIds);

    public sealed record MemberListResponse(
        List<BotMember> Members);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_ISpaces");

        group.MapGet("/Get", async (Guid spaceId) =>
        {
            var space = grains.GetGrain<ISpaceGrain>(spaceId);
            var data  = await space.GetSpace();
            return Results.Ok(new BotSpaceDetail(
                data.Id,
                data.Name,
                data.Description,
                data.IsCommunity));
        });

        group.MapGet("/ListMembers", async (Guid spaceId) =>
        {
            var space   = grains.GetGrain<ISpaceGrain>(spaceId);
            var members = await space.GetMembers();
            return Results.Ok(new MemberListResponse(
                members.Select(m => new BotMember(
                    m.member.userId,
                    m.member.spaceId,
                    m.member.user.username,
                    m.member.user.displayName,
                    m.member.archetypes.Values.Select(a => a.archetypeId).ToList()
                )).ToList()));
        });

        group.MapGet("/GetMember", async (Guid spaceId, Guid userId) =>
        {
            var space  = grains.GetGrain<ISpaceGrain>(spaceId);
            var member = await space.GetMember(userId);
            return Results.Ok(new BotMember(
                member.member.userId,
                member.member.spaceId,
                member.member.user.username,
                member.member.user.displayName,
                member.member.archetypes.Values.Select(a => a.archetypeId).ToList()));
        });
    }
}
