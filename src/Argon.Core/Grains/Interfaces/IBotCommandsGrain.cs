namespace Argon.Grains.Interfaces;

using Argon.Core.Entities.Data;

[Alias($"Argon.Grains.Interfaces.{nameof(IBotCommandsGrain)}")]
public interface IBotCommandsGrain : IGrainWithGuidKey
{
    [Alias(nameof(Register))]
    Task<RegisterCommandResult> Register(string name, string description, Guid? spaceId,
        List<BotCommandOption>? options, bool defaultPermission);

    [Alias(nameof(Update))]
    Task<UpdateCommandResult> Update(Guid commandId, string? description,
        List<BotCommandOption>? options, bool? defaultPermission);

    [Alias(nameof(Delete))]
    Task<bool> Delete(Guid commandId);

    [Alias(nameof(List))]
    Task<List<BotCommandInfo>> List();

    [Alias(nameof(ListForSpace))]
    Task<List<BotCommandInfo>> ListForSpace(Guid spaceId);
}

[GenerateSerializer, Immutable]
public sealed record BotCommandInfo(
    [property: Id(0)] Guid                   CommandId,
    [property: Id(1)] string                 Name,
    [property: Id(2)] string                 Description,
    [property: Id(3)] Guid?                  SpaceId,
    [property: Id(4)] bool                   DefaultPermission,
    [property: Id(5)] List<BotCommandOption> Options);

[GenerateSerializer, Immutable]
public sealed record RegisterCommandResult(
    [property: Id(0)] bool   Success,
    [property: Id(1)] string? Error = null,
    [property: Id(2)] Guid?  CommandId = null,
    [property: Id(3)] string? Name = null,
    [property: Id(4)] Guid?  SpaceId = null);

[GenerateSerializer, Immutable]
public sealed record UpdateCommandResult(
    [property: Id(0)] bool           Success,
    [property: Id(1)] string?        Error = null,
    [property: Id(2)] BotCommandInfo? Command = null);
