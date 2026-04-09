namespace Argon.Grains.Interfaces;

[Alias($"Argon.Grains.Interfaces.{nameof(IBotDirectoryGrain)}")]
public interface IBotDirectoryGrain : IGrainWithGuidKey
{
    [Alias(nameof(FindByUsername))]
    Task<BotSearchInfo?> FindByUsername(string username);

    [Alias(nameof(GetBotDetails))]
    Task<BotDetailInfo?> GetBotDetails(Guid botAppId);
}

[GenerateSerializer, Immutable]
public sealed record BotSearchInfo(
    [property: Id(0)] Guid   AppId,
    [property: Id(1)] string Name,
    [property: Id(2)] string Username,
    [property: Id(3)] string? Description,
    [property: Id(4)] string? AvatarFileId,
    [property: Id(5)] bool   IsVerified,
    [property: Id(6)] List<string> RequiredScopes);

[GenerateSerializer, Immutable]
public sealed record BotDetailInfo(
    [property: Id(0)] Guid   AppId,
    [property: Id(1)] string Name,
    [property: Id(2)] string Username,
    [property: Id(3)] string? Description,
    [property: Id(4)] string? AvatarFileId,
    [property: Id(5)] bool   IsVerified,
    [property: Id(6)] bool   IsPublic,
    [property: Id(7)] List<string> RequiredScopes,
    [property: Id(8)] int    MaxSpaces,
    [property: Id(9)] string TeamName);
