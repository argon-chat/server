namespace Argon.Api.BotApi.Interfaces;

using Argon.Core.Entities.Data;
using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;
using Argon.Grains.Interfaces;

[BotInterface("ICommands", 1)]
[BotDescription("Register, update, list, and delete slash commands for your bot.")]
public sealed class CommandsV1(IGrainFactory grains) : IBotInterface
{
    public sealed record RegisterCommandRequest(
        string                  Name,
        string                  Description,
        Guid?                   SpaceId           = null,
        List<BotCommandOption>? Options           = null,
        bool?                   DefaultPermission = null);

    public sealed record UpdateCommandRequest(
        Guid                    CommandId,
        string?                 Description       = null,
        List<BotCommandOption>? Options           = null,
        bool?                   DefaultPermission = null);

    public sealed record DeleteCommandQuery(Guid CommandId);

    public sealed record SpaceQuery(Guid SpaceId);

    public sealed record CommandRegisteredResponse(
        Guid   CommandId,
        string Name,
        Guid?  SpaceId);

    public sealed record BotCommand(
        Guid                   CommandId,
        string                 Name,
        string                 Description,
        Guid?                  SpaceId,
        bool                   DefaultPermission,
        List<BotCommandOption> Options);

    public sealed record CommandListResponse(
        List<BotCommand> Commands);

    private static readonly BotError InvalidName        = new(400, "invalid_name", "Name must be 1-32 lowercase alphanumeric characters.");
    private static readonly BotError InvalidDescription = new(400, "invalid_description", "Description must be max 100 characters.");
    private static readonly BotError CommandLimit       = new(400, "command_limit", "Maximum 50 commands per scope.");
    private static readonly BotError CommandNotFound    = new(404, "not_found", "Command does not exist or is not owned by this bot.");

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();
        group.RequireRateLimiting("Bot_ICommands");

        group.Post<RegisterCommandRequest, CommandRegisteredResponse>("/Register")
           .Summary("Registers a new slash command. Commands can be global or scoped to a specific space. Max 50 commands per scope.")
           .Throws(InvalidName)
           .Throws(InvalidDescription)
           .Throws(CommandLimit)
           .Handle(async (ctx, request) =>
            {
                var grain  = grains.GetGrain<IBotCommandsGrain>(ctx.GetBotAppId());
                var result = await grain.Register(
                    request.Name, request.Description, request.SpaceId,
                    request.Options, request.DefaultPermission ?? true);

                if (!result.Success)
                    throw (result.Error switch
                    {
                        "command_limit"       => CommandLimit,
                        "invalid_description" => InvalidDescription,
                        _                     => InvalidName
                    }).Raise();

                return new CommandRegisteredResponse(result.CommandId!.Value, result.Name!, result.SpaceId);
            });

        group.Patch<UpdateCommandRequest, BotCommand>("/Update")
           .Summary("Updates an existing slash command's description, options, or default permission.")
           .Throws(CommandNotFound)
           .Handle(async (ctx, request) =>
            {
                var grain  = grains.GetGrain<IBotCommandsGrain>(ctx.GetBotAppId());
                var result = await grain.Update(
                    request.CommandId, request.Description,
                    request.Options, request.DefaultPermission);

                if (!result.Success)
                    throw CommandNotFound.Raise();

                return Describe(result.Command!);
            });

        group.Delete<DeleteCommandQuery, DeletedResponse>("/Delete")
           .Summary("Deletes a slash command by its commandId.")
           .Throws(CommandNotFound)
           .Handle(async (ctx, query) =>
            {
                var grain = grains.GetGrain<IBotCommandsGrain>(ctx.GetBotAppId());

                if (!await grain.Delete(query.CommandId))
                    throw CommandNotFound.Raise();

                return new DeletedResponse(true);
            });

        group.Get<CommandListResponse>("/List")
           .Summary("Lists all commands registered by this bot across all scopes.")
           .Handle(async ctx =>
            {
                var commands = await grains.GetGrain<IBotCommandsGrain>(ctx.GetBotAppId()).List();

                return new CommandListResponse(commands.Select(Describe).ToList());
            });

        group.Get<SpaceQuery, CommandListResponse>("/ListForSpace")
           .Summary("Lists commands available in a specific space (global + space-scoped).")
           .Handle(async (ctx, query) =>
            {
                var commands = await grains.GetGrain<IBotCommandsGrain>(ctx.GetBotAppId())
                   .ListForSpace(query.SpaceId);

                return new CommandListResponse(commands.Select(Describe).ToList());
            });
    }

    private static BotCommand Describe(BotCommandInfo command)
        => new(
            command.CommandId, command.Name, command.Description,
            command.SpaceId, command.DefaultPermission, command.Options);
}
