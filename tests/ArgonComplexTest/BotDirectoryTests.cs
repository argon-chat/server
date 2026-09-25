namespace ArgonComplexTest.Tests;

using System.Net;
using Argon.Core.Entities.Data;
using Argon.Grains.Interfaces;
using ArgonContracts;
using static DevTeamsHarness;

/// <summary>
/// How a bot is recognised — by its token on the Bot API — and the slash commands it registers.
/// </summary>
/// <remarks>
/// <para>Token resolution is exercised over HTTP, because <c>BotTokenAuthenticationHandler</c> is the
/// only caller of <c>IBotDirectoryGrain.ResolveByToken</c> and every malformed token has to end as the
/// same 401 a wrong one does — never a 500 from a parse that threw. The one input HTTP cannot deliver,
/// an empty token, the handler refuses itself; the grain is asked directly to show it would too.</para>
///
/// <para>Commands go straight to <c>IBotCommandsGrain</c>: <c>BotApiTests</c> covers the HTTP mapping,
/// and what is left is the grain's own rules — the per-scope limit and the fact that a bot can only
/// touch its own commands.</para>
/// </remarks>
[TestFixture]
public class BotDirectoryTests : TestBase
{
    private IBotDirectoryGrain Directory => GetGrainFactory().GetGrain<IBotDirectoryGrain>(Guid.Empty);

    /// <summary>
    /// Every malformed or wrong bot token is refused as unauthorised; the right one resolves to its bot.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_malformed_or_wrong_token_is_refused_like_any_other(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "tokens");
        var bot   = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Token Bot", BotUsername("tok"), ct));
        var token = bot.botDetails!.botToken;
        var hex   = token[..token.IndexOf(':')];

        var cases = new Dictionary<string, string>
        {
            ["missing colon"]  = token.Replace(":", ""),
            ["short app id"]   = $"{hex[..31]}:{token[33..]}",
            ["non-hex app id"] = $"{new string('z', 32)}:{token[33..]}",
            ["wrong secret"]   = $"{hex}:{new string('A', 43)}",
            ["no secret"]      = $"{hex}:",
            ["unknown app id"] = $"{Guid.NewGuid():N}:{token[33..]}"
        };

        foreach (var (name, candidate) in cases)
        {
            Assert.That(await BotGetMeAsync(candidate, ct), Is.EqualTo(HttpStatusCode.Unauthorized),
                $"a token with a {name} was not refused as unauthorised");
        }

        var resolved = await Directory.ResolveByToken(token);
        var empty    = await Directory.ResolveByToken(string.Empty);
        var genuine  = await BotGetMeAsync(token, ct);

        Assert.Multiple(() =>
        {
            Assert.That(empty, Is.Null);
            Assert.That(genuine, Is.EqualTo(HttpStatusCode.OK));

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.AppId, Is.EqualTo(bot.appId));
            Assert.That(resolved.BotAsUserId, Is.EqualTo(bot.appId), "a console-created bot's account shares its app id");
            Assert.That(resolved.TeamId, Is.EqualTo(team.teamId));
            Assert.That(resolved.BotName, Is.EqualTo("Token Bot"));
            Assert.That(resolved.IsRestricted, Is.False);
            Assert.That(resolved.MaxSpaces, Is.EqualTo(5));
        });
    }

    /// <summary>
    /// A bot holds at most fifty commands per scope — globally, and separately in each space.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_bot_holds_at_most_fifty_commands_per_scope(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var team     = await CreateTeamAsync(owner, "cmdlimit");
        var bot      = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Busy Bot", BotUsername("busy"), ct));
        var commands = GetGrainFactory().GetGrain<IBotCommandsGrain>(bot.appId);
        var spaceId  = Guid.NewGuid();

        for (var i = 0; i < 50; i++)
        {
            var registered = await commands.Register($"cmd{i:D2}", "filler", null, null, true);
            Assert.That(registered.Success, Is.True, $"command {i} of fifty was refused: {registered.Error}");
        }

        var overGlobal = await commands.Register("onetoomany", "over the limit", null, null, true);
        var blankName  = await commands.Register("  ", "no name", spaceId, null, true);
        var longName   = await commands.Register(new string('n', 33), "too long a name", spaceId, null, true);
        var longText   = await commands.Register("wordy", new string('d', 101), spaceId, null, true);
        var inSpace    = await commands.Register("spaceonly", "a different scope", spaceId, null, true);
        var listed     = await commands.List();

        Assert.Multiple(() =>
        {
            Assert.That(overGlobal, Is.EqualTo(new RegisterCommandResult(false, "command_limit")));
            Assert.That(blankName, Is.EqualTo(new RegisterCommandResult(false, "invalid_name")));
            Assert.That(longName, Is.EqualTo(new RegisterCommandResult(false, "invalid_name")));
            Assert.That(longText, Is.EqualTo(new RegisterCommandResult(false, "invalid_description")));
            Assert.That(inSpace.Success, Is.True, "the global limit was applied to a space's own commands");
            Assert.That(inSpace.SpaceId, Is.EqualTo(spaceId));
            Assert.That(listed, Has.Count.EqualTo(51));
            Assert.That(listed.Select(c => c.Name), Is.Ordered, "commands are not listed by name");
        });
    }

    /// <summary>
    /// Registering a name a bot already uses in the same scope leaves it with one command of that name,
    /// and answers rather than failing.
    /// </summary>
    /// <remarks>
    /// <para><b>Open, needs a decision.</b> <c>BotCommandsGrain.Register</c> never looks for an
    /// existing command of the same name. The unique index on <c>(AppId, SpaceId, Name)</c> catches it
    /// for a space-scoped command — as a <c>DbUpdateException</c>, which the Bot API turns into a 500 —
    /// and does not catch it at all for a global one, because <c>SpaceId</c> is null there and a null
    /// never collides in a unique index on either engine. So a bot that re-registers its commands on
    /// every start, which is the ordinary thing to do, piles up duplicate global commands and crashes on
    /// its space ones.</para>
    ///
    /// <para>What it should do instead is the open part: refuse the duplicate, which needs an error code
    /// the stable <c>ICommands/v1</c> contract does not have, or overwrite it in place, which changes
    /// what "Register" means. Either closes this test.</para>
    /// </remarks>
    [Test, CancelAfter(120_000), Category("KnownPresenceBug")]
    public async Task Registering_a_taken_command_name_keeps_one_command(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var team     = await CreateTeamAsync(owner, "cmddup");
        var bot      = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Twice Bot", BotUsername("twice"), ct));
        var commands = GetGrainFactory().GetGrain<IBotCommandsGrain>(bot.appId);
        var spaceId  = Guid.NewGuid();

        await commands.Register("ping", "first", null, null, true);
        await commands.Register("ping", "second", null, null, true);

        await commands.Register("pong", "first", spaceId, null, true);

        Exception? failed = null;
        try
        {
            await commands.Register("pong", "second", spaceId, null, true);
        }
        catch (Exception e)
        {
            failed = e;
        }

        var global = (await commands.List()).Where(c => c.SpaceId is null && c.Name == "ping").ToList();
        var scoped = (await commands.ListForSpace(spaceId)).Where(c => c.SpaceId == spaceId && c.Name == "pong").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(global, Has.Count.EqualTo(1), "a bot ended up with two global commands of the same name");
            Assert.That(failed, Is.Null, $"registering a taken space-scoped name failed instead of answering: {failed?.GetType().Name}");
            Assert.That(scoped, Has.Count.EqualTo(1));
        });
    }

    /// <summary>
    /// A command's description, options and default permission can each be changed on their own, and
    /// only by the bot that owns it.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_command_is_edited_field_by_field_and_only_by_its_own_bot(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var admin   = await CreateSessionAsync(ct);
        var team    = await CreateTeamAsync(owner, "commands");
        var bot     = await CreatePublishedBotAsync(owner, team.teamId, "cmd");
        var (spaceId, _) = await CreateSpaceWithChannelAsync(admin, "Commands", ct);
        await InstallAsync(admin, spaceId, bot.appId, ct);

        var mine  = GetGrainFactory().GetGrain<IBotCommandsGrain>(bot.appId);
        var other = GetGrainFactory().GetGrain<IBotCommandsGrain>(Guid.NewGuid());

        var registered = await mine.Register("Roll", "Rolls a die", spaceId, null, true);
        var commandId  = registered.CommandId!.Value;

        var dice = new List<BotCommandOption>
        {
            new() { Name = "sides", Description = "How many sides", Type = BotCommandOptionType.Integer, Required = true }
        };

        var hijackUpdate = await other.Update(commandId, "mine now", null, false);
        var hijackDelete = await other.Delete(commandId);

        var withOptions    = await mine.Update(commandId, null, dice, null);
        var withPermission = await mine.Update(commandId, null, null, false);
        var withText       = await mine.Update(commandId, "Rolls any die", null, null);
        var missing        = await mine.Update(Guid.NewGuid(), "nothing", null, null);

        var seen = await admin.Client.ForService<IBotManagementInteraction>(FactoryAsp.Services).GetSpaceCommands(spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(registered.Name, Is.EqualTo("roll"), "a command name is not stored lower-case");
            Assert.That(hijackUpdate, Is.EqualTo(new UpdateCommandResult(false, "not_found")),
                "one bot edited another bot's command");
            Assert.That(hijackDelete, Is.False, "one bot deleted another bot's command");
            Assert.That(missing, Is.EqualTo(new UpdateCommandResult(false, "not_found")));

            Assert.That(withOptions.Command!.Description, Is.EqualTo("Rolls a die"), "an options-only edit changed the description");
            Assert.That(withOptions.Command.Options.Single().Name, Is.EqualTo("sides"));
            Assert.That(withOptions.Command.DefaultPermission, Is.True);
            Assert.That(withPermission.Command!.DefaultPermission, Is.False);
            Assert.That(withPermission.Command.Options, Has.Count.EqualTo(1), "a permission-only edit dropped the options");
            Assert.That(withText.Command!.Description, Is.EqualTo("Rolls any die"));
            Assert.That(withText.Command.DefaultPermission, Is.False, "a description-only edit reset the permission");

            var command = seen.Single(c => c.commandId == commandId);
            Assert.That(command.appId, Is.EqualTo(bot.appId));
            Assert.That(command.name, Is.EqualTo("roll"));
            Assert.That(command.options.Single().type, Is.EqualTo(CommandOptionType.Integer));
            Assert.That(command.options.Single().required, Is.True);
        });

        Assert.That(await mine.Delete(commandId), Is.True);
        Assert.That(await mine.ListForSpace(spaceId), Is.Empty);
    }
}
