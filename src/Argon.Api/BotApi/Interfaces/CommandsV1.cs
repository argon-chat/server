namespace Argon.Api.BotApi.Interfaces;

using Argon.Core.Entities.Data;
using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;

[BotInterface("ICommands", 1)]
[BotDescription("Register, update, list, and delete slash commands for your bot.")]
[StableContract("0c93ed62652e9b973dd2f36606148bf1131cff23d3eb5b4199d64af5138e016b")]
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
public sealed class CommandsV1(IGrainFactory grains, IServiceProvider serviceProvider) : IBotInterface
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

            if (request.Name.Length > 32 || string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new BotApiError("invalid_name", "Name must be 1-32 characters"));
            if (request.Description.Length > 100)
                return Results.BadRequest(new BotApiError("invalid_description", "Description must be max 100 characters"));

            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var existingCount = await db.BotCommands
               .Where(c => c.AppId == appId && c.SpaceId == request.SpaceId)
               .CountAsync();

            if (existingCount >= 50)
                return Results.BadRequest(new BotApiError("command_limit", "Maximum 50 commands per scope"));

            var entity = new BotCommandEntity
            {
                CommandId         = Guid.NewGuid(),
                AppId             = appId,
                SpaceId           = request.SpaceId,
                Name              = request.Name.ToLowerInvariant(),
                Description       = request.Description,
                Options           = request.Options ?? [],
                DefaultPermission = request.DefaultPermission ?? true
            };

            db.BotCommands.Add(entity);
            await db.SaveChangesAsync();

            return Results.Ok(new CommandRegisteredResponse(entity.CommandId, entity.Name, entity.SpaceId));
        });

        group.MapPatch("/Update", async (HttpContext ctx, UpdateCommandRequest request) =>
        {
            var appId = ctx.GetBotAppId();

            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var command = await db.BotCommands
               .FirstOrDefaultAsync(c => c.CommandId == request.CommandId && c.AppId == appId);

            if (command is null)
                return Results.NotFound(new BotApiError("not_found"));

            if (request.Description is not null)
                command.Description = request.Description;
            if (request.Options is not null)
                command.Options = request.Options;
            if (request.DefaultPermission.HasValue)
                command.DefaultPermission = request.DefaultPermission.Value;
            command.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync();
            return Results.Ok(new BotCommand(
                command.CommandId, command.Name, command.Description,
                command.SpaceId, command.DefaultPermission, command.Options));
        });

        group.MapDelete("/Delete", async (HttpContext ctx, Guid commandId) =>
        {
            var appId = ctx.GetBotAppId();

            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var deleted = await db.BotCommands
               .Where(c => c.CommandId == commandId && c.AppId == appId)
               .ExecuteDeleteAsync();

            return deleted > 0
                ? Results.Ok(new DeletedResponse(true))
                : Results.NotFound(new BotApiError("not_found"));
        });

        group.MapGet("/List", async (HttpContext ctx) =>
        {
            var appId = ctx.GetBotAppId();

            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var commands = await db.BotCommands
               .AsNoTracking()
               .Where(c => c.AppId == appId)
               .OrderBy(c => c.Name)
               .ToListAsync();

            return Results.Ok(new CommandListResponse(
                commands.Select(c => new BotCommand(
                    c.CommandId, c.Name, c.Description,
                    c.SpaceId, c.DefaultPermission, c.Options)).ToList()));
        });

        group.MapGet("/ListForSpace", async (HttpContext ctx, Guid spaceId) =>
        {
            var appId = ctx.GetBotAppId();

            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var commands = await db.BotCommands
               .AsNoTracking()
               .Where(c => c.AppId == appId && (c.SpaceId == null || c.SpaceId == spaceId))
               .OrderBy(c => c.Name)
               .ToListAsync();

            return Results.Ok(new CommandListResponse(
                commands.Select(c => new BotCommand(
                    c.CommandId, c.Name, c.Description,
                    c.SpaceId, c.DefaultPermission, c.Options)).ToList()));
        });
    }
}
