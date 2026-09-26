namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Headers;
using AccountContracts;
using Argon.Api.Features.AccountConsole;
using ArgonComplexTest.Infrastructure;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The developer half of the account console — <c>ITeamConsole</c>, <c>IAppManagement</c> and the
/// access gate in front of both — as one caller sees it.
/// </summary>
public sealed record DevConsole(ITeamConsole Teams, IAppManagement Apps, ITeamAccessChecker Access);

/// <summary>
/// Drives the developer console the way <see cref="AccountConsoleHarness"/> drives the account half,
/// and for the same reasons: the console listens on a port of its own behind an OIDC interceptor the
/// integration host has no provider for, and that interceptor's only output is the ambient request
/// context set here.
/// </summary>
public static class DevTeamsHarness
{
    /// <summary>
    /// Runs <paramref name="call"/> with <paramref name="who"/>'s identity in scope.
    /// </summary>
    /// <remarks>
    /// The context is an <c>AsyncLocal</c>, and this method is <c>async</c>: whatever it sets lives in
    /// its own execution context and is gone when it returns. So every call names its caller, and a
    /// test switching between two developers cannot leak one identity into the other's call.
    /// </remarks>
    public static async Task<T> As<T>(TestUserSession who, Func<DevConsole, Task<T>> call)
    {
        var (scope, _) = AccountConsoleHarness.Console(who);

        await using (scope)
        {
            var services = scope.ServiceProvider;

            return await call(new DevConsole(
                services.GetRequiredService<ITeamConsole>(),
                services.GetRequiredService<IAppManagement>(),
                services.GetRequiredService<ITeamAccessChecker>()));
        }
    }

    /// <inheritdoc cref="As{T}"/>
    public static Task As(TestUserSession who, Func<DevConsole, Task> call)
        => As(who, async console =>
        {
            await call(console);
            return true;
        });

    /// <summary>A team name no other fixture or earlier run can collide with.</summary>
    public static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 13, 40)];

    /// <summary>A bot username that is free and ends in "bot", as the console requires.</summary>
    public static string BotUsername(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 12)] + "bot";

    /// <summary>A team created through the console by <paramref name="owner"/>.</summary>
    public static Task<TeamDetails> CreateTeamAsync(TestUserSession owner, string prefix = "team")
        => As(owner, c => c.Teams.CreateTeam(UniqueName(prefix)));

    /// <summary>A bot app created through the console, published so a space can install it.</summary>
    public static async Task<AppDetails> CreatePublishedBotAsync(TestUserSession owner, Guid teamId, string prefix = "dt")
    {
        var bot = await As(owner, c => c.Apps.CreateBotApp(teamId, "DevTeams Bot", BotUsername(prefix)).Ok());
        await As(owner, c => c.Apps.PublishBot(teamId, bot.appId).Ok());
        return bot;
    }

    /// <summary>Installs a bot into a space as its owner, and fails the test if the install is refused.</summary>
    public static async Task InstallAsync(TestUserSession spaceOwner, Guid spaceId, Guid botAppId, CancellationToken ct = default)
    {
        var result = await spaceOwner.Client
           .ForService<IBotManagementInteraction>(ArgonTestEnvironment.Instance.Host.Services)
           .InstallBot(spaceId, botAppId, ct);

        Assert.That(result, Is.InstanceOf<SuccessInstallBot>(),
            $"the bot could not be installed: {(result as FailedInstallBot)?.error}");
    }

    /// <summary>A private space owned by <paramref name="owner"/>, with one text channel in it.</summary>
    public static async Task<(Guid SpaceId, Guid ChannelId)> CreateSpaceWithChannelAsync(
        TestUserSession owner, string name, CancellationToken ct = default)
    {
        var created = await owner.Users.CreateSpace(new CreateServerRequest(name, "devteams", string.Empty), ct);

        Assert.That(created, Is.InstanceOf<SuccessCreateSpace>(),
            $"could not create a space: {(created as FailedCreateSpace)?.error}");

        var spaceId = ((SuccessCreateSpace)created).space.spaceId;

        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, "bots", ChannelType.Text, "for the bot", null), ct).Ok();

        var channels = await owner.Channels.GetChannels(spaceId, Guid.Empty, ct);

        return (spaceId, channels.First(c => c.channel.name == "bots").channel.channelId);
    }

    /// <summary>What the Bot API answers a bot presenting <paramref name="token"/> on <c>IBotSelf/GetMe</c>.</summary>
    public static async Task<HttpStatusCode> BotGetMeAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/bot/IBotSelf/v1/GetMe");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);

        using var response = await ArgonTestEnvironment.Instance.HttpClient.SendAsync(request, ct);
        return response.StatusCode;
    }
}
