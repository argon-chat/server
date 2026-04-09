namespace Argon.Grains;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using Orleans.Concurrency;

[StatelessWorker]
public class BotCommandsGrain(
    IDbContextFactory<ApplicationDbContext> context
) : Grain, IBotCommandsGrain
{
    private Guid AppId => this.GetPrimaryKey();

    public async Task<RegisterCommandResult> Register(string name, string description, Guid? spaceId,
        List<BotCommandOption>? options, bool defaultPermission)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 32)
            return new RegisterCommandResult(false, "invalid_name");
        if (description.Length > 100)
            return new RegisterCommandResult(false, "invalid_description");

        await using var db = await context.CreateDbContextAsync();

        var existingCount = await db.BotCommands
           .Where(c => c.AppId == AppId && c.SpaceId == spaceId)
           .CountAsync();

        if (existingCount >= 50)
            return new RegisterCommandResult(false, "command_limit");

        var entity = new BotCommandEntity
        {
            CommandId         = Guid.NewGuid(),
            AppId             = AppId,
            SpaceId           = spaceId,
            Name              = name.ToLowerInvariant(),
            Description       = description,
            Options           = options ?? [],
            DefaultPermission = defaultPermission
        };

        db.BotCommands.Add(entity);
        await db.SaveChangesAsync();

        return new RegisterCommandResult(true, CommandId: entity.CommandId, Name: entity.Name, SpaceId: entity.SpaceId);
    }

    public async Task<UpdateCommandResult> Update(Guid commandId, string? description,
        List<BotCommandOption>? options, bool? defaultPermission)
    {
        await using var db = await context.CreateDbContextAsync();

        var command = await db.BotCommands
           .FirstOrDefaultAsync(c => c.CommandId == commandId && c.AppId == AppId);

        if (command is null)
            return new UpdateCommandResult(false, "not_found");

        if (description is not null)
            command.Description = description;
        if (options is not null)
            command.Options = options;
        if (defaultPermission.HasValue)
            command.DefaultPermission = defaultPermission.Value;
        command.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync();

        return new UpdateCommandResult(true, Command: new BotCommandInfo(
            command.CommandId, command.Name, command.Description,
            command.SpaceId, command.DefaultPermission, command.Options));
    }

    public async Task<bool> Delete(Guid commandId)
    {
        await using var db = await context.CreateDbContextAsync();

        var deleted = await db.BotCommands
           .Where(c => c.CommandId == commandId && c.AppId == AppId)
           .ExecuteDeleteAsync();

        return deleted > 0;
    }

    public async Task<List<BotCommandInfo>> List()
    {
        await using var db = await context.CreateDbContextAsync();

        return await db.BotCommands
           .AsNoTracking()
           .Where(c => c.AppId == AppId)
           .OrderBy(c => c.Name)
           .Select(c => new BotCommandInfo(
                c.CommandId, c.Name, c.Description,
                c.SpaceId, c.DefaultPermission, c.Options))
           .ToListAsync();
    }

    public async Task<List<BotCommandInfo>> ListForSpace(Guid spaceId)
    {
        await using var db = await context.CreateDbContextAsync();

        return await db.BotCommands
           .AsNoTracking()
           .Where(c => c.AppId == AppId && (c.SpaceId == null || c.SpaceId == spaceId))
           .OrderBy(c => c.Name)
           .Select(c => new BotCommandInfo(
                c.CommandId, c.Name, c.Description,
                c.SpaceId, c.DefaultPermission, c.Options))
           .ToListAsync();
    }
}
