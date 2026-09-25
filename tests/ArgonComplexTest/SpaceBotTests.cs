namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argon.Core.Entities.Data;
using Argon.Entities;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using static SpaceGroupSupport;

/// <summary>
/// Installing a bot into a space, taking it out again, and approving what it asks for — the owner's
/// half of the bot lifecycle, which <c>SpaceGrain</c> owns.
/// </summary>
/// <remarks>
/// <para>An install is a membership plus a locked, hidden role carrying exactly the entitlements the
/// bot declared. When the developer later widens that declaration, the space keeps granting the old
/// set until the owner approves the new one — "pending approval" is the gap between the two, and the
/// listing is where the owner sees it.</para>
///
/// <para>Every test seeds its own bot straight into the database, the way <c>BotApiTests</c> does:
/// there is no public flow that creates one, and the install counts a bot's spaces, so sharing one
/// would make the limit tests depend on their neighbours.</para>
/// </remarks>
[TestFixture]
public class SpaceBotTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    private const ArgonEntitlement Asked = ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages;

    private sealed record SeededBot(Guid AppId, Guid UserId, string Token, string Name);

    private static async Task<SeededBot> SeedBotAsync(
        TestUserSession developer,
        ArgonEntitlement required = Asked,
        int maxSpaces = 100,
        BotLifecycleState state = BotLifecycleState.Published,
        CancellationToken ct = default)
    {
        var botUserId = Guid.NewGuid();
        var botAppId  = Guid.NewGuid();
        var teamId    = Guid.NewGuid();
        var token     = BotToken(botAppId);
        var name      = Unique("SpaceBot");

        await using var db = await NewDbAsync(ct);

        db.Users.Add(new UserEntity
        {
            Id          = botUserId,
            Username    = $"sbot_{botUserId:N}"[..32],
            DisplayName = name,
            Email       = $"sbot_{botUserId:N}@test.local",
            AgreeTOS    = true,
            DateOfBirth = new DateOnly(2000, 1, 1)
        });
        db.TeamEntities.Add(new DevTeamEntity { TeamId = teamId, OwnerId = developer.UserId, Name = "Space bot team" });
        db.MemberTeamEntities.Add(new DevTeamMemberEntity
        {
            TeamId = teamId, UserId = developer.UserId, JoinedAt = DateTime.UtcNow, IsOwner = true
        });
        db.BotEntities.Add(new BotEntity
        {
            AppId                = botAppId,
            TeamId               = teamId,
            Name                 = name,
            ClientId             = Guid.NewGuid().ToString(),
            ClientSecret         = Guid.NewGuid().ToString(),
            AppType              = DevAppType.Bot,
            BotToken             = token,
            BotAsUserId          = botUserId,
            LifecycleState       = state,
            MaxSpaces            = maxSpaces,
            RequiredEntitlements = required,
            RequiredScopes       = [],
            AllowedRedirects     = []
        });

        await db.SaveChangesAsync(ct);
        return new SeededBot(botAppId, botUserId, token, name);
    }

    /// <summary>The <c>{hex app id}:{secret}</c> shape the bot authentication handler parses.</summary>
    private static string BotToken(Guid botAppId)
    {
        Span<byte> app = stackalloc byte[16];
        botAppId.TryWriteBytes(app);
        app.Reverse();

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"{Convert.ToHexString(app)}:{secret}";
    }

    private static async Task<InstalledBotInfo> InstallAsync(TestUserSession owner, Guid spaceId, Guid appId, CancellationToken ct)
    {
        var result = await Bots(owner).InstallBot(spaceId, appId, ct);

        Assert.That(result, Is.InstanceOf<SuccessInstallBot>(), $"could not install the bot: {(result as FailedInstallBot)?.error}");
        return ((SuccessInstallBot)result).bot;
    }

    private static async Task<List<Guid>> RosterAsync(TestUserSession viewer, Guid spaceId, CancellationToken ct)
        => (await viewer.Servers.GetSpaceSnapshot(spaceId, null, ct)).members!.Value.Values.Select(m => m.userId).ToList();

    // ── Installing ──────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task InstallBot_ListsTheBotWithTheRoleItAskedFor(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Bot home", ct);
        var bot     = await SeedBotAsync(owner, ct: ct);

        var before    = await Bots(owner).GetInstalledBots(spaceId, ct);
        var installed = await InstallAsync(owner, spaceId, bot.AppId, ct);
        var listed    = (await Bots(owner).GetInstalledBots(spaceId, ct)).Values.Single();
        var roles     = await Archetypes(owner).GetServerArchetypes(spaceId, ct);
        var roster    = await RosterAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(before.Values, Is.Empty);
            Assert.That((installed.appId, installed.botUserId, installed.pendingApproval), Is.EqualTo((bot.AppId, bot.UserId, false)));
            Assert.That((listed.appId, listed.name, listed.requiredEntitlements, listed.grantedEntitlements, listed.pendingApproval),
                Is.EqualTo((bot.AppId, bot.Name, Asked, Asked, false)));
            Assert.That(roles.Values.Where(r => r.name == $"Bot: {bot.Name}").Select(r => (r.isLocked, r.entitlement)),
                Is.EqualTo(new[] { (true, Asked) }), "the install did not create the bot's locked role");
            Assert.That(roster, Does.Contain(bot.UserId));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task InstallBot_IsRefusedWhenItCannotHappen(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);
        var first  = await CreateSpaceAsync(owner, "First", ct);
        var second = await CreateSpaceAsync(owner, "Second", ct);
        await JoinAsync(owner, member, first, ct);

        var single = await SeedBotAsync(owner, maxSpaces: 1, ct: ct);
        var draft  = await SeedBotAsync(owner, state: BotLifecycleState.Development, ct: ct);
        var bots   = Bots(owner);

        var byMember = await Bots(member).InstallBot(first, single.AppId, ct);
        var unknown  = await bots.InstallBot(first, Guid.NewGuid(), ct);
        var inDraft  = await bots.InstallBot(first, draft.AppId, ct);
        var noSpace  = await bots.InstallBot(Guid.NewGuid(), single.AppId, ct);

        await InstallAsync(owner, first, single.AppId, ct);

        var again    = await bots.InstallBot(first, single.AppId, ct);
        var overFull = await bots.InstallBot(second, single.AppId, ct);

        static InstallBotError? Error(IInstallBotResult r) => (r as FailedInstallBot)?.error;

        Assert.Multiple(() =>
        {
            Assert.That(Error(byMember), Is.EqualTo(InstallBotError.INSUFFICIENT_PERMISSIONS));
            Assert.That(Error(unknown), Is.EqualTo(InstallBotError.NOT_FOUND));
            Assert.That(Error(inDraft), Is.EqualTo(InstallBotError.NOT_FOUND), "a bot that is not published was installed");
            Assert.That(Error(noSpace), Is.EqualTo(InstallBotError.NOT_FOUND));
            Assert.That(Error(again), Is.EqualTo(InstallBotError.ALREADY_INSTALLED));
            Assert.That(Error(overFull), Is.EqualTo(InstallBotError.BOT_SPACE_LIMIT));
        });
    }

    // ── Uninstalling ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An uninstall takes the bot off the roster, takes its role away, and says so to the space.
    /// </summary>
    /// <remarks>
    /// The install announces the bot's arrival through the ordinary join path; the uninstall deleted
    /// the membership and dropped the cached roster but announced nothing, so every client already in
    /// the space kept listing the bot until it next reloaded the space.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task UninstallBot_TakesTheBotAndItsRoleOutAndTellsTheSpace(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Bot leaves", ct);
        var bot     = await SeedBotAsync(owner, ct: ct);
        await InstallAsync(owner, spaceId, bot.AppId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(owner, ct);
        await watcher.SubscribeToSpace(spaceId, ct);
        var mark = watcher.Mark();

        var result = await Bots(owner).UninstallBot(spaceId, bot.AppId, ct);
        Assert.That(result, Is.InstanceOf<SuccessUninstallBot>(), $"{(result as FailedUninstallBot)?.error}");

        var announced = await watcher.FirstWithinAsync<LeavedFromServerUser>(e => e.userId == bot.UserId, EventWait, mark, ct);
        var listed    = await Bots(owner).GetInstalledBots(spaceId, ct);
        var roster    = await RosterAsync(owner, spaceId, ct);
        var roles     = await Archetypes(owner).GetServerArchetypes(spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(announced, Is.Not.Null, "the space was never told the bot left");
            Assert.That(listed.Values, Is.Empty);
            Assert.That(roster, Does.Not.Contain(bot.UserId));
            Assert.That(roles.Values.Select(r => r.name), Does.Not.Contain($"Bot: {bot.Name}"), "the bot's role outlived the bot");
        });

        // Gone completely: the same bot installs again as if for the first time.
        var reinstalled = await InstallAsync(owner, spaceId, bot.AppId, ct);
        Assert.That(reinstalled.grantedEntitlements, Is.EqualTo(Asked));
    }

    [Test, CancelAfter(120_000)]
    public async Task UninstallBot_IsRefusedWhenThereIsNothingToUninstall(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var member  = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Nothing to remove", ct);
        await JoinAsync(owner, member, spaceId, ct);

        var installed    = await SeedBotAsync(owner, ct: ct);
        var notInstalled = await SeedBotAsync(owner, ct: ct);
        await InstallAsync(owner, spaceId, installed.AppId, ct);

        static UninstallBotError? Error(IUninstallBotResult r) => (r as FailedUninstallBot)?.error;

        var byMember = await Bots(member).UninstallBot(spaceId, installed.AppId, ct);
        var unknown  = await Bots(owner).UninstallBot(spaceId, Guid.NewGuid(), ct);
        var absent   = await Bots(owner).UninstallBot(spaceId, notInstalled.AppId, ct);
        var noSpace  = await Bots(owner).UninstallBot(Guid.NewGuid(), installed.AppId, ct);
        var roster   = await RosterAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(Error(byMember), Is.EqualTo(UninstallBotError.INSUFFICIENT_PERMISSIONS));
            Assert.That(Error(unknown), Is.EqualTo(UninstallBotError.NOT_FOUND));
            Assert.That(Error(absent), Is.EqualTo(UninstallBotError.NOT_INSTALLED));
            Assert.That(Error(noSpace), Is.EqualTo(UninstallBotError.NOT_FOUND));
            Assert.That(roster, Does.Contain(installed.UserId), "a refused uninstall removed the bot anyway");
        });
    }

    // ── Approving a widened request ─────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task ApproveBotEntitlements_GrantsWhatTheBotNowAsksFor(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Widened", ct);
        var bot     = await SeedBotAsync(owner, ct: ct);
        await InstallAsync(owner, spaceId, bot.AppId, ct);

        const ArgonEntitlement widened = Asked | ArgonEntitlement.AddReactions;

        await using (var db = await NewDbAsync(ct))
            await db.BotEntities.Where(b => b.AppId == bot.AppId)
               .ExecuteUpdateAsync(s => s.SetProperty(b => b.RequiredEntitlements, widened), ct);

        var pending = (await Bots(owner).GetInstalledBots(spaceId, ct)).Values.Single();

        await using var watcher = await RealtimeClient.ConnectAsync(owner, ct);
        await watcher.SubscribeToSpace(spaceId, ct);
        var mark = watcher.Mark();

        var approved  = await Bots(owner).ApproveBotEntitlements(spaceId, bot.AppId, ct);
        var announced = await watcher.WaitForAsync<ArchetypeChanged>(e => e.data.name == $"Bot: {bot.Name}", EventWait, mark, ct);
        var settled   = (await Bots(owner).GetInstalledBots(spaceId, ct)).Values.Single();
        var again     = await Bots(owner).ApproveBotEntitlements(spaceId, bot.AppId, ct);

        Assert.Multiple(() =>
        {
            Assert.That((pending.grantedEntitlements, pending.pendingApproval), Is.EqualTo((Asked, true)));
            Assert.That(approved, Is.InstanceOf<SuccessApproval>(), $"{(approved as FailedApproval)?.error}");
            Assert.That(announced.data.entitlement, Is.EqualTo(widened));
            Assert.That((settled.grantedEntitlements, settled.pendingApproval), Is.EqualTo((widened, false)));
            Assert.That((again as FailedApproval)?.error, Is.EqualTo(ApproveBotEntitlementsError.ALREADY_UP_TO_DATE));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task ApproveBotEntitlements_IsRefusedWhenThereIsNothingToApprove(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var member  = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Nothing to approve", ct);
        await JoinAsync(owner, member, spaceId, ct);

        var installed    = await SeedBotAsync(owner, ct: ct);
        var notInstalled = await SeedBotAsync(owner, ct: ct);
        var roleless     = await SeedBotAsync(owner, ct: ct);
        await InstallAsync(owner, spaceId, installed.AppId, ct);
        await InstallAsync(owner, spaceId, roleless.AppId, ct);

        // A bot member whose locked role assignment is gone: nothing left to widen.
        await using (var db = await NewDbAsync(ct))
            await db.MemberArchetypes
               .Where(ma => ma.ServerMember.SpaceId == spaceId && ma.ServerMember.UserId == roleless.UserId)
               .ExecuteDeleteAsync(ct);

        static ApproveBotEntitlementsError? Error(IApproveBotEntitlementsResult r) => (r as FailedApproval)?.error;

        var bots     = Bots(owner);
        var byMember = await Bots(member).ApproveBotEntitlements(spaceId, installed.AppId, ct);
        var unknown  = await bots.ApproveBotEntitlements(spaceId, Guid.NewGuid(), ct);
        var absent   = await bots.ApproveBotEntitlements(spaceId, notInstalled.AppId, ct);
        var noSpace  = await bots.ApproveBotEntitlements(Guid.NewGuid(), installed.AppId, ct);
        var noRole   = await bots.ApproveBotEntitlements(spaceId, roleless.AppId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(Error(byMember), Is.EqualTo(ApproveBotEntitlementsError.INSUFFICIENT_PERMISSIONS));
            Assert.That(Error(unknown), Is.EqualTo(ApproveBotEntitlementsError.NOT_FOUND));
            Assert.That(Error(absent), Is.EqualTo(ApproveBotEntitlementsError.NOT_INSTALLED));
            Assert.That(Error(noSpace), Is.EqualTo(ApproveBotEntitlementsError.NOT_FOUND));
            Assert.That(Error(noRole), Is.EqualTo(ApproveBotEntitlementsError.NOT_FOUND));
        });
    }

    /// <summary>
    /// A bot that is connected while it is installed or uninstalled is told so on its own stream.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_connected_bot_is_told_it_was_installed_and_uninstalled(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var home   = await CreateSpaceAsync(owner, "Bot home", ct);
        var second = await CreateSpaceAsync(owner, "Bot visit", ct);
        var bot    = await SeedBotAsync(owner, ct: ct);
        await InstallAsync(owner, home, bot.AppId, ct);

        await using var stream = await BotStream.OpenAsync(HttpClient, bot.Token, ct);

        await InstallAsync(owner, second, bot.AppId, ct);
        var installed = await stream.WaitForAsync("botInstallingToSpace", second, EventWait, ct);

        var removed     = await Bots(owner).UninstallBot(second, bot.AppId, ct);
        var uninstalled = await stream.WaitForAsync("botUninstallingFromSpace", second, EventWait, ct);

        Assert.Multiple(() =>
        {
            Assert.That(installed, Is.True, $"no install event reached the connected bot: {stream.Dump()}");
            Assert.That(removed, Is.InstanceOf<SuccessUninstallBot>());
            Assert.That(uninstalled, Is.True, $"no uninstall event reached the connected bot: {stream.Dump()}");
        });
    }

    /// <summary>One open <c>IEvents/v1/Stream</c>, recording the name and data of every event on it.</summary>
    private sealed class BotStream : IAsyncDisposable
    {
        private readonly CancellationTokenSource   cts;
        private readonly HttpResponseMessage       response;
        private readonly TaskCompletionSource      ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(string Name, string Data)> events = [];
        private readonly Task                      pump;

        private BotStream(CancellationTokenSource cts, HttpResponseMessage response, Stream body)
        {
            this.cts      = cts;
            this.response = response;
            pump          = PumpAsync(body);
        }

        public static async Task<BotStream> OpenAsync(HttpClient http, string token, CancellationToken ct)
        {
            var cts     = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/bot/IEvents/v1/Stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.That(response.IsSuccessStatusCode, Is.True, $"the bot event stream was refused: {response.StatusCode}");

            var stream = new BotStream(cts, response, await response.Content.ReadAsStreamAsync(cts.Token));
            await stream.ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            return stream;
        }

        private async Task PumpAsync(Stream body)
        {
            try
            {
                using var reader = new StreamReader(body, Encoding.UTF8);
                string? name = null;

                while (await reader.ReadLineAsync(cts.Token) is { } line)
                {
                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                    {
                        name = line[7..];
                        if (name == "ready")
                            ready.TrySetResult();
                    }
                    else if (line.StartsWith("data: ", StringComparison.Ordinal) && name is not null)
                    {
                        lock (events)
                            events.Add((name, line[6..]));
                    }
                }
            }
            catch
            {
                // Disposal ends the stream; there is nothing to report about that.
            }
            finally
            {
                ready.TrySetCanceled();
            }
        }

        public async Task<bool> WaitForAsync(string name, Guid mentioning, TimeSpan timeout, CancellationToken ct)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;

            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (events)
                {
                    if (events.Any(e => e.Name == name && e.Data.Contains(mentioning.ToString(), StringComparison.OrdinalIgnoreCase)))
                        return true;
                }

                await Task.Delay(50, ct);
            }

            return false;
        }

        public string Dump()
        {
            lock (events)
                return string.Join("; ", events.Select(e => $"{e.Name} {e.Data}"));
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            response.Dispose();

            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch
            {
                // Already accounted for by the pump.
            }

            cts.Dispose();
        }
    }

    // ── A bot as a member ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A member row with no profile row behind it is answered with the placeholder rather than failing
    /// the rest of the batch. A bot seeded without a profile is exactly that.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task PrefetchProfiles_ForAMemberWithNoProfile_AnswersAPlaceholderAndTheRest(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "No profile", ct);
        var bot     = await SeedBotAsync(owner, ct: ct);
        await InstallAsync(owner, spaceId, bot.AppId, ct);

        var profiles = await owner.Servers.PrefetchProfiles(spaceId, new IonArray<Guid>([bot.UserId, owner.UserId]), ct);

        Assert.Multiple(() =>
        {
            Assert.That(profiles.Values.Select(p => p.userId), Is.EqualTo(new[] { bot.UserId, owner.UserId }));
            Assert.That(profiles.Values[0].bio, Is.EqualTo("Deleted Account"));
            Assert.That(profiles.Values[1].bio, Is.Not.EqualTo("Deleted Account"));
        });
    }

    /// <summary>
    /// A bot creating a channel through the bot API gets a 400 for a channel the grain refuses, not a
    /// 500 — the same validation the client path gets.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_bot_creating_an_invalid_channel_is_told_why(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Bot builds", ct);
        var bot     = await SeedBotAsync(owner, Asked | ArgonEntitlement.ManageChannels, ct: ct);
        await InstallAsync(owner, spaceId, bot.AppId, ct);

        async Task<HttpResponseMessage> CreateAsync(string name)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/bot/IChannels/v1/Create");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", bot.Token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { spaceId, name, channelType = 0, description = "by a bot" }),
                Encoding.UTF8, "application/json");
            return await HttpClient.SendAsync(request, ct);
        }

        using var blank = await CreateAsync("   ");
        using var fine  = await CreateAsync("built-by-bot");

        var blankBody = await blank.Content.ReadAsStringAsync(ct);
        var fineBody  = await fine.Content.ReadAsStringAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(blank.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), blankBody);
            Assert.That(blankBody, Does.Contain("validation_error"));
            Assert.That(fine.StatusCode, Is.EqualTo(HttpStatusCode.OK), fineBody);
        });

        Assert.That((await ChannelsAsync(owner, spaceId, ct)).Select(c => c.name), Is.EqualTo(new[] { "built-by-bot" }));
    }
}
