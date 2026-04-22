namespace Argon.Services.Ion;

using Argon.Core.Entities.Data;
using ArgonContracts;
using ion.runtime;

public class BotManagementInteractionImpl : IBotManagementInteraction
{
    public async Task<IonArray<BotSearchResult>> SearchBots(Guid spaceId, string query, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IBotDirectoryGrain>(Guid.Empty).FindByUsername(query);
        if (result is null)
            return new([]);
        return new([new BotSearchResult(
            result.AppId, result.Name, result.Username, result.Description, result.AvatarFileId,
            result.IsVerified, new IonArray<string>(result.RequiredScopes))]);
    }

    public async Task<BotDetails> GetBotDetails(Guid spaceId, Guid botAppId, CancellationToken ct = default)
    {
        var d = await this.GetGrain<IBotDirectoryGrain>(Guid.Empty).GetBotDetails(botAppId);
        if (d is null)
            throw new InvalidOperationException("Bot does not exist or is not public.");
        return new BotDetails(
            d.AppId, d.Name, d.Username, d.Description, d.AvatarFileId,
            d.IsVerified, d.IsPublic, new IonArray<string>(d.RequiredScopes),
            d.MaxSpaces, d.TeamName);
    }

    public async Task<IonArray<InstalledBotInfo>> GetInstalledBots(Guid spaceId, CancellationToken ct = default)
    {
        var bots = await this.GetGrain<ISpaceGrain>(spaceId).GetInstalledBots();
        return new(bots.Select(b => new InstalledBotInfo(
            b.AppId, b.Name, b.Username, b.AvatarFileId, b.IsVerified, b.BotUserId)).ToList());
    }

    public async Task<IInstallBotResult> InstallBot(Guid spaceId, Guid botAppId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId).InstallBot(botAppId);
        if (!result.Success)
            return new FailedInstallBot(result.Error!.Value);
        var b = result.Bot!;
        return new SuccessInstallBot(new InstalledBotInfo(
            b.AppId, b.Name, b.Username, b.AvatarFileId, b.IsVerified, b.BotUserId));
    }

    public async Task<IUninstallBotResult> UninstallBot(Guid spaceId, Guid botAppId, CancellationToken ct = default)
    {
        var result = await this.GetGrain<ISpaceGrain>(spaceId).UninstallBot(botAppId);
        if (!result.Success)
            return new FailedUninstallBot(result.Error!.Value);
        return new SuccessUninstallBot();
    }

    public async Task<IonArray<SpaceCommand>> GetSpaceCommands(Guid spaceId, CancellationToken ct = default)
    {
        var installedBots = await this.GetGrain<ISpaceGrain>(spaceId).GetInstalledBots();
        var allCommands = new List<SpaceCommand>();

        foreach (var bot in installedBots)
        {
            var commands = await this.GetGrain<IBotCommandsGrain>(bot.AppId).ListForSpace(spaceId);
            foreach (var cmd in commands)
            {
                allCommands.Add(new SpaceCommand(
                    cmd.CommandId,
                    bot.AppId,
                    cmd.Name,
                    cmd.Description,
                    cmd.Options.Select(o => new SpaceCommandOption(
                        o.Name,
                        o.Description,
                        (CommandOptionType)(int)o.Type,
                        o.Required
                    )).ToList()
                ));
            }
        }

        return new(allCommands);
    }
}
