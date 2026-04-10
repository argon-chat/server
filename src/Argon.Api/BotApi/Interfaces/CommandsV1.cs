namespace Argon.Api.BotApi.Interfaces;

using Argon.Core.Entities.Data;
using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;
using Argon.Grains.Interfaces;

[BotInterface("ICommands", 1)]
[BotDescription("Register, update, list, and delete slash commands for your bot.")]
[StableContract("9287d91b57d858e8f8d10a5d3fb4a5dde39adf9af17a33da4e41517d15fc365e")]
[BotRoute("POST",   "/Register",     RequestType = typeof(RegisterCommandRequest), ResponseType = typeof(CommandRegisteredResponse), Description = "Registers a new slash command. Commands can be global or scoped to a specific space. Max 50 commands per scope.")]
[BotRoute("PATCH",  "/Update",       RequestType = typeof(UpdateCommandRequest),   ResponseType = typeof(BotCommand), Description = "Updates an existing slash command's description, options, or default permission.")]
[BotRoute("DELETE", "/Delete",       ResponseType = typeof(DeletedResponse), Description = "Deletes a slash command by its commandId (query parameter).")]
[BotRoute("GET",    "/List",         ResponseType = typeof(CommandListResponse), Description = "Lists all commands registered by this bot across all scopes.")]
[BotRoute("GET",    "/ListForSpace", ResponseType = typeof(CommandListResponse), Description = "Lists commands available in a specific space (global + space-scoped). Pass spaceId as a query parameter.")]
[BotError("/Register", 400, "invalid_name", "Name must be 1-32 lowercase alphanumeric characters.")]
[BotError("/Register", 400, "invalid_description", "Description must be max 100 characters.")]
[BotError("/Register", 400, "command_limit", "Maximum 50 commands per scope.")]
[BotError("/Update", 404, "not_found", "Command does not exist or is not owned by this bot.")]
[BotError("/Delete", 404, "not_found", "Command does not exist or is not owned by this bot.")]
public sealed class CommandsV1(IGrainFactory grains) : IBotInterface
{
    public sealed record RegisterCommandRequest(
        string                    Name,
        string                    Description,
        Guid?                     SpaceId           = null,
        List<BotCommandOption>?   Options           = null,
        bool?                     DefaultPermission = null);

    public sealed record UpdateCommandRequest(
        Guid                      CommandId,
        string?                   Description       = null,
        List<BotCommandOption>?   Options           = null,
        bool?                     DefaultPermission = null);

    public sealed record CommandRegisteredResponse(
        Guid    CommandId,
        string  Name,
        Guid?   SpaceId);

    public sealed record BotCommand(
        Guid                    CommandId,
        string                  Name,
        string                  Description,
        Guid?                   SpaceId,
        bool                    DefaultPermission,
        List<BotCommandOption>  Options);

    public sealed record CommandListResponse(
        List<BotCommand> Commands);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.AddEndpointFilter<BotOrleansPropagationFilter>();

        group.MapPost("/Register", async (HttpContext ctx, RegisterCommandRequest request) =>
        {
            var appId = ctx.GetBotAppId();
            var grain = grains.GetGrain<IBotCommandsGrain>(appId);

            var result = await grain.Register(
                request.Name, request.Description, request.SpaceId,
                request.Options, request.DefaultPermission ?? true);

            if (!result.Success)
                return result.Error switch
                {
                    "command_limit" => Results.BadRequest(new BotApiError("command_limit", "Maximum 50 commands per scope")),
                    "invalid_description" => Results.BadRequest(new BotApiError("invalid_description", "Description must be max 100 characters")),
                    _ => Results.BadRequest(new BotApiError("invalid_name", "Name must be 1-32 characters"))
                };

            return Results.Ok(new CommandRegisteredResponse(result.CommandId!.Value, result.Name!, result.SpaceId));
        });

        group.MapPatch("/Update", async (HttpContext ctx, UpdateCommandRequest request) =>
        {
            var appId = ctx.GetBotAppId();
            var grain = grains.GetGrain<IBotCommandsGrain>(appId);

            var result = await grain.Update(
                request.CommandId, request.Description,
                request.Options, request.DefaultPermission);

            if (!result.Success)
                return Results.NotFound(new BotApiError("not_found"));

            var c = result.Command!;
            return Results.Ok(new BotCommand(
                c.CommandId, c.Name, c.Description,
                c.SpaceId, c.DefaultPermission, c.Options));
        });

        group.MapDelete("/Delete", async (HttpContext ctx, Guid commandId) =>
        {
            var appId = ctx.GetBotAppId();
            var grain = grains.GetGrain<IBotCommandsGrain>(appId);

            var deleted = await grain.Delete(commandId);

            return deleted
                ? Results.Ok(new DeletedResponse(true))
                : Results.NotFound(new BotApiError("not_found"));
        });

        group.MapGet("/List", async (HttpContext ctx) =>
        {
            var appId = ctx.GetBotAppId();
            var grain = grains.GetGrain<IBotCommandsGrain>(appId);

            var commands = await grain.List();

            return Results.Ok(new CommandListResponse(
                commands.Select(c => new BotCommand(
                    c.CommandId, c.Name, c.Description,
                    c.SpaceId, c.DefaultPermission, c.Options)).ToList()));
        });

        group.MapGet("/ListForSpace", async (HttpContext ctx, Guid spaceId) =>
        {
            var appId = ctx.GetBotAppId();
            var grain = grains.GetGrain<IBotCommandsGrain>(appId);

            var commands = await grain.ListForSpace(spaceId);

            return Results.Ok(new CommandListResponse(
                commands.Select(c => new BotCommand(
                    c.CommandId, c.Name, c.Description,
                    c.SpaceId, c.DefaultPermission, c.Options)).ToList()));
        });
    }
}
