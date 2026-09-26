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
/// and what is left is the grain's own rules — the per-scope limit, registration as an upsert, and the
/// fact that a bot can only touch its own commands.</para>
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
        var bot   = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Token Bot", BotUsername("tok"), ct).Ok());
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
        var bot      = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Busy Bot", BotUsername("busy"), ct).Ok());
        var commands = GetGrainFactory().GetGrain<IBotCommandsGrain>(bot.appId);
        var spaceId  = Guid.NewGuid();

        for (var i = 0; i < 50; i++)
        {
            var registered = await commands.Register($"cmd{i:D2}", "filler", null, null, true);
            Assert.That(registered.Success, Is.True, $"command {i} of fifty was refused: {registered.Error}");
        }

        var overGlobal = await commands.Register("onetoomany", "over the limit", null, null, true);
        var refreshed  = await commands.Register("cmd07", "refreshed at the limit", null, null, false);
        var blankName  = await commands.Register("  ", "no name", spaceId, null, true);
        var longName   = await commands.Register(new string('n', 33), "too long a name", spaceId, null, true);
        var longText   = await commands.Register("wordy", new string('d', 101), spaceId, null, true);
        var inSpace    = await commands.Register("spaceonly", "a different scope", spaceId, null, true);
        var listed     = await commands.List();

        Assert.Multiple(() =>
        {
            Assert.That(overGlobal, Is.EqualTo(new RegisterCommandResult(false, "command_limit")));
            Assert.That(refreshed.Success, Is.True, "re-registering a command the bot already has was refused at the limit");
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
    /// Registering a name a bot already uses in the same scope updates that command instead of adding a
    /// second one — globally, where the unique index cannot help, as well as in a space.
    /// </summary>
    /// <remarks>
    /// A bot re-registers its commands on every start, which is the ordinary thing to do. Before
    /// <c>Register</c> became an upsert that piled up duplicate global commands, because a null
    /// <c>SpaceId</c> never collides in the <c>(AppId, SpaceId, Name)</c> index, and failed the space ones
    /// with a <c>DbUpdateException</c> the Bot API answered as a 500. A command of the same name in a
    /// different scope is a different command and is left alone.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Registering_a_taken_command_name_keeps_one_command(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var team     = await CreateTeamAsync(owner, "cmddup");
        var bot      = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Twice Bot", BotUsername("twice"), ct).Ok());
        var commands = GetGrainFactory().GetGrain<IBotCommandsGrain>(bot.appId);
        var spaceId  = Guid.NewGuid();

        var option = new List<BotCommandOption>
        {
            new() { Name = "target", Description = "Who to ping", Type = BotCommandOptionType.User, Required = true }
        };

        var first  = await commands.Register("ping", "first", null, null, true);
        var second = await commands.Register("PING", "second", null, option, false);

        var spaceFirst = await commands.Register("pong", "first", spaceId, null, true);

        Exception? failed = null;
        RegisterCommandResult? spaceSecond = null;
        try
        {
            spaceSecond = await commands.Register("pong", "second", spaceId, null, true);
        }
        catch (Exception e)
        {
            failed = e;
        }

        var inSpaceToo = await commands.Register("ping", "space ping", spaceId, null, true);

        var all    = await commands.List();
        var global = all.Where(c => c.SpaceId is null && c.Name == "ping").ToList();
        var scoped = all.Where(c => c.SpaceId == spaceId && c.Name == "pong").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(global, Has.Count.EqualTo(1), "a bot ended up with two global commands of the same name");
            Assert.That(second.Success, Is.True);
            Assert.That(second.CommandId, Is.EqualTo(first.CommandId), "the re-registration did not answer with the existing command");
            Assert.That(second.Name, Is.EqualTo("ping"));
            Assert.That(global.Single().Description, Is.EqualTo("second"), "the re-registration did not update the command");
            Assert.That(global.Single().Options.Single().Name, Is.EqualTo("target"));
            Assert.That(global.Single().DefaultPermission, Is.False);

            Assert.That(failed, Is.Null, $"registering a taken space-scoped name failed instead of answering: {failed?.GetType().Name}");
            Assert.That(spaceSecond?.CommandId, Is.EqualTo(spaceFirst.CommandId));
            Assert.That(scoped, Has.Count.EqualTo(1));
            Assert.That(scoped.Single().Description, Is.EqualTo("second"));

            Assert.That(inSpaceToo.CommandId, Is.Not.EqualTo(first.CommandId),
                "a space command was folded into the global command of the same name");
            Assert.That(all, Has.Count.EqualTo(3));
        });
    }

    /// <summary>
    /// Duplicates left behind before registration became an upsert collapse into the oldest of them the
    /// next time the bot registers that name.
    /// </summary>
    /// <remarks>Seeded, because nothing can produce a duplicate any more.</remarks>
    [Test, CancelAfter(120_000)]
    public async Task Duplicates_from_before_the_upsert_collapse_on_the_next_registration(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var team     = await CreateTeamAsync(owner, "cmdlegacy");
        var bot      = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Legacy Bot", BotUsername("legacy"), ct).Ok());
        var commands = GetGrainFactory().GetGrain<IBotCommandsGrain>(bot.appId);

        var oldest = Guid.NewGuid();

        await using (var db = await ArgonComplexTest.Infrastructure.Account.AccountSeed.NewDbAsync(ct))
        {
            db.BotCommands.AddRange(
                new BotCommandEntity
                {
                    CommandId = oldest, AppId = bot.appId, Name = "legacy", Description = "one",
                    CreatedAt = DateTime.UtcNow.AddDays(-2)
                },
                new BotCommandEntity
                {
                    CommandId = Guid.NewGuid(), AppId = bot.appId, Name = "legacy", Description = "two",
                    CreatedAt = DateTime.UtcNow.AddDays(-1)
                });

            await db.SaveChangesAsync(ct);
        }

        var registered = await commands.Register("legacy", "the one left", null, null, true);
        var listed     = (await commands.List()).Where(c => c.Name == "legacy").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(registered.CommandId, Is.EqualTo(oldest), "the oldest duplicate was not the one kept");
            Assert.That(listed.Select(c => (c.CommandId, c.Description)), Is.EqualTo(new[] { (oldest, "the one left") }));
        });
    }

    /// <summary>
    /// Over the Bot API, registering the same command twice answers twice with the same command.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task The_bot_api_answers_a_re_registration_with_the_same_command(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var team    = await CreateTeamAsync(owner, "cmdhttp");
        var bot     = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Http Bot", BotUsername("http"), ct).Ok());
        var token   = bot.botDetails!.botToken;
        var spaceId = Guid.NewGuid();

        var (firstStatus, firstId)   = await RegisterOverHttpAsync(token, "roll", "first", spaceId, ct);
        var (secondStatus, secondId) = await RegisterOverHttpAsync(token, "roll", "second", spaceId, ct);

        var listed = await GetGrainFactory().GetGrain<IBotCommandsGrain>(bot.appId).ListForSpace(spaceId);

        Assert.Multiple(() =>
        {
            Assert.That(firstStatus, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(secondStatus, Is.EqualTo(HttpStatusCode.OK), "registering a command the bot already has failed");
            Assert.That(secondId, Is.EqualTo(firstId));
            Assert.That(listed.Select(c => (c.Name, c.Description)), Is.EqualTo(new[] { ("roll", "second") }));
        });
    }

    private async Task<(HttpStatusCode Status, string? CommandId)> RegisterOverHttpAsync(
        string token, string name, string description, Guid spaceId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bot/ICommands/v1/Register")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { name, description, spaceId })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", token);

        using var response = await HttpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
            return (response.StatusCode, null);

        var body = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<System.Text.Json.JsonElement>(response.Content, ct);
        return (response.StatusCode, body.GetProperty("commandId").GetString());
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
