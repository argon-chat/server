namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;

[BotInterface("ISpaces", 1)]
[BotDescription("Get space details and member information.")]
public sealed class SpacesV1(IGrainFactory grains) : IBotInterface
{
    public sealed record SpaceQuery(Guid SpaceId);

    public sealed record MemberQuery(Guid SpaceId, Guid UserId);

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

        group.Get<SpaceQuery, BotSpaceDetail>("/Get")
           .Summary("Gets space details — name, description, and community flag.")
           .Handle(async (_, query) =>
            {
                var space = await grains.GetGrain<ISpaceGrain>(query.SpaceId).GetSpace();

                return new BotSpaceDetail(space.Id, space.Name, space.Description, space.IsCommunity);
            });

        group.Get<SpaceQuery, MemberListResponse>("/ListMembers")
           .Summary("Lists all members of a space with their username, display name, and roles.")
           .Privileged()
           .Handle(async (_, query) =>
            {
                var members = await grains.GetGrain<ISpaceReadGrain>(query.SpaceId).GetMembers();

                return new MemberListResponse(members.Select(Describe).ToList());
            });

        group.Get<MemberQuery, BotMember>("/GetMember")
           .Summary("Gets a single member's details.")
           .Handle(async (_, query)
                => Describe(await grains.GetGrain<ISpaceGrain>(query.SpaceId).GetMember(query.UserId)));
    }

    private static BotMember Describe(RealtimeServerMember member)
        => new(
            member.member.userId,
            member.member.spaceId,
            member.member.user.username,
            member.member.user.displayName,
            member.member.archetypes.Values.Select(a => a.archetypeId).ToList());
}
