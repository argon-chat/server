namespace ArgonComplexTest.Tests;

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Orleans.Runtime;

/// <summary>
/// What the channel fixtures share: a space with channels in it, members with the two archetypes
/// that matter, direct grain calls made as a given user, and a bot seeded the way
/// <c>BotApiTests</c> seeds one.
/// </summary>
internal static class ChannelTestKit
{
    public static IServiceProvider Services => ArgonTestEnvironment.Instance.Host.Services;

    public static IGrainFactory Grains => Services.GetRequiredService<IGrainFactory>();

    public static async Task<Guid> CreateSpaceAsync(TestUserSession owner, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest("Channel Space", "Description", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)?.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    public static async Task<Guid> CreateChannelAsync(TestUserSession owner, Guid spaceId, string name, ChannelType kind,
        CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty, new CreateChannelRequest(spaceId, name, kind, "Test channel", null), ct).Ok();

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var created  = channels.Values.FirstOrDefault(c => c.channel.name == name);

        if (created is null)
        {
            Assert.Fail($"Failed to find created channel '{name}'");
            return Guid.Empty;
        }

        return created.channel.channelId;
    }

    /// <summary>Puts <paramref name="guest"/> in the space as an ordinary member — "everyone" only.</summary>
    public static async Task JoinAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(), $"Guest could not join: {(joined as FailedJoin)?.error}");
    }

    public static IArchetypeInteraction ArchetypesOf(TestUserSession session)
        => session.Client.ForService<IArchetypeInteraction>(Services);

    public static async Task<Archetype> EveryoneAsync(TestUserSession owner, Guid spaceId, CancellationToken ct)
        => (await ArchetypesOf(owner).GetServerArchetypes(spaceId, ct)).Values.First(a => a.isDefault);

    /// <summary>Takes <paramref name="entitlement"/> away from "everyone" on one channel only.</summary>
    public static async Task DenyOnChannelAsync(TestUserSession owner, Guid spaceId, Guid channelId, ArgonEntitlement entitlement,
        CancellationToken ct)
    {
        var everyone = await EveryoneAsync(owner, spaceId, ct);
        await ArchetypesOf(owner).UpsertArchetypeEntitlementForChannel(spaceId, channelId, everyone.id,
            deny: entitlement, allow: ArgonEntitlement.None, ct);
    }

    public static async Task<Guid> MemberIdOfAsync(TestUserSession owner, Guid spaceId, Guid userId, CancellationToken ct)
        => (await owner.Servers.GetMembers(spaceId, ct)).Values.First(m => m.member.userId == userId).member.memberId;

    /// <summary>
    /// Calls a grain as <paramref name="userId"/>, the way the Ion and bot layers do: the caller travels
    /// in the request context, which is what <c>GetUserId()</c> reads on the far side.
    /// </summary>
    public static async Task<T> AsUserAsync<T>(Guid userId, Func<Task<T>> call)
    {
        RequestContext.Set("$caller_user_id", userId);
        try
        {
            return await call();
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    public static async Task AsUserAsync(Guid userId, Func<Task> call)
        => await AsUserAsync(userId, async () =>
        {
            await call();
            return true;
        });

    public static async Task<ApplicationDbContext> DbAsync(CancellationToken ct)
        => await Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync(ct);

    /// <summary>The stored row, soft-deleted or not — the read path hides the former.</summary>
    public static async Task<ArgonMessageEntity?> StoredMessageAsync(Guid spaceId, Guid channelId, long messageId, CancellationToken ct)
    {
        await using var db = await DbAsync(ct);
        return await db.Messages.AsNoTracking()
           .FirstOrDefaultAsync(m => m.SpaceId == spaceId && m.ChannelId == channelId && m.MessageId == messageId, ct);
    }

    /// <summary>The mention count one member's read state holds for a channel, as the badge endpoint serves it.</summary>
    public static async Task<int> MentionsAsync(TestUserSession member, Guid channelId, CancellationToken ct)
    {
        var badges = await member.Users.GetGlobalBadges(ct);
        return badges.readStates.Values.FirstOrDefault(r => r.channelId == channelId)?.mentionCount ?? 0;
    }

    /// <summary>Polls until <paramref name="accept"/> holds or the window ends, and returns the last value seen.</summary>
    public static async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> accept, TimeSpan window, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + window;

        while (true)
        {
            var value = await read();

            if (accept(value) || DateTimeOffset.UtcNow >= deadline)
                return value;

            await Task.Delay(100, ct);
        }
    }
}

/// <summary>
/// A bot seeded straight into the database — there is no public flow that creates one — and put in a
/// space as an ordinary member, plus the bot HTTP surface and its event stream.
/// </summary>
internal sealed class ChannelTestBot
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public Guid   AppId  { get; }
    public Guid   UserId { get; }
    public string Token  { get; }

    private ChannelTestBot(Guid appId, Guid userId, string token)
    {
        AppId  = appId;
        UserId = userId;
        Token  = token;
    }

    public static async Task<ChannelTestBot> SeedAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var botUserId = Guid.NewGuid();
        var botAppId  = Guid.NewGuid();
        var botToken  = GenerateToken(botAppId);
        var teamId    = Guid.NewGuid();

        await using var db = await ChannelTestKit.DbAsync(ct);

        db.Users.Add(new UserEntity
        {
            Id          = botUserId,
            Username    = $"cbot_{botUserId:N}"[..32],
            DisplayName = "Channel Bot",
            Email       = $"cbot_{botUserId:N}@test.local",
            AgreeTOS    = true,
            DateOfBirth = new DateOnly(2000, 1, 1)
        });

        db.TeamEntities.Add(new DevTeamEntity { TeamId = teamId, OwnerId = ownerUserId, Name = "Channel Bot Team" });
        db.MemberTeamEntities.Add(new DevTeamMemberEntity
        {
            TeamId = teamId, UserId = ownerUserId, JoinedAt = DateTime.UtcNow, IsOwner = true
        });

        db.BotEntities.Add(new BotEntity
        {
            AppId            = botAppId,
            TeamId           = teamId,
            Name             = "Channel Bot App",
            ClientId         = Guid.NewGuid().ToString(),
            ClientSecret     = Guid.NewGuid().ToString(),
            AppType          = DevAppType.Bot,
            BotToken         = botToken,
            BotAsUserId      = botUserId,
            LifecycleState   = BotLifecycleState.Published,
            MaxSpaces        = 100,
            RequiredScopes   = [],
            AllowedRedirects = []
        });

        await db.SaveChangesAsync(ct);

        return new ChannelTestBot(botAppId, botUserId, botToken);
    }

    /// <summary>Joins the space as the bot user, the way <c>BotApiTests</c> does.</summary>
    public async Task JoinAsync(Guid spaceId)
    {
        RequestContext.Set("$caller_user_id", UserId);
        RequestContext.Set("$caller_user_ip", "127.0.0.1");
        RequestContext.Set("$caller_machine_id", $"bot:{AppId}");

        try
        {
            await ChannelTestKit.Grains.GetGrain<ISpaceGrain>(spaceId).DoJoinUserAsync();
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    public async Task<HttpResponseMessage> CallAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, $"/api/bot{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", Token);

        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        return await ArgonTestEnvironment.Instance.HttpClient.SendAsync(request, ct);
    }

    public async Task<JsonElement> CallOkAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var response = await CallAsync(method, path, body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        Assert.That(response.IsSuccessStatusCode, Is.True, $"{method} {path} answered {(int)response.StatusCode}: {text}");

        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A bot message with controls, through <c>IMessages/v1/Send</c>.</summary>
    public async Task<long> SendAsync(Guid spaceId, Guid channelId, string text, object[]? controls = null, CancellationToken ct = default)
    {
        var sent = await CallOkAsync(HttpMethod.Post, "/IMessages/v1/Send", new
        {
            spaceId,
            channelId,
            text,
            randomId = Random.Shared.NextInt64(1, long.MaxValue),
            controls
        }, ct);

        return sent.GetProperty("messageId").GetInt64();
    }

    public Task<BotEvents> OpenEventsAsync(CancellationToken ct) => BotEvents.OpenAsync(Token, ct);

    private static string GenerateToken(Guid botAppId)
    {
        Span<byte> appBytes = stackalloc byte[16];
        botAppId.TryWriteBytes(appBytes);
        appBytes.Reverse();

        Span<byte> secretBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(secretBytes);

        var secret = Convert.ToBase64String(secretBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        return $"{Convert.ToHexString(appBytes)}:{secret}";
    }

    /// <summary>
    /// One open <c>IEvents/v1/Stream</c>, recording every frame. Returned only once the gateway has
    /// written <c>ready</c>, so anything published afterwards has a subscription to land in.
    /// </summary>
    internal sealed class BotEvents : IAsyncDisposable
    {
        private readonly CancellationTokenSource        cts;
        private readonly HttpResponseMessage            response;
        private readonly TaskCompletionSource           ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<(string Name, JObject Data)> frames = [];
        private readonly Lock                           gate  = new();
        private readonly Task                           pump;

        private BotEvents(CancellationTokenSource cts, HttpResponseMessage response, Stream body)
        {
            this.cts      = cts;
            this.response = response;
            pump          = PumpAsync(body);
        }

        public static async Task<BotEvents> OpenAsync(string token, CancellationToken ct)
        {
            var cts     = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/bot/IEvents/v1/Stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await ArgonTestEnvironment.Instance.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Assert.That(response.IsSuccessStatusCode, Is.True, $"the bot event stream was refused: {(int)response.StatusCode}");

            var events = new BotEvents(cts, response, await response.Content.ReadAsStreamAsync(cts.Token));

            try
            {
                await events.ready.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            }
            catch (Exception e)
            {
                await events.DisposeAsync();
                Assert.Fail($"the bot event stream never reported 'ready': {e.Message}");
            }

            return events;
        }

        private async Task PumpAsync(Stream body)
        {
            try
            {
                using var reader = new StreamReader(body, Encoding.UTF8);
                string? name = null;

                while (!cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    if (line is null)
                        break;

                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                        name = line[7..];
                    else if (line.StartsWith("data: ", StringComparison.Ordinal) && name is not null)
                    {
                        lock (gate)
                            frames.Add((name, JObject.Parse(line[6..])));

                        if (name == "ready")
                            ready.TrySetResult();

                        name = null;
                    }
                }
            }
            catch
            {
                // Disposal ends the stream; nothing to report.
            }
            finally
            {
                ready.TrySetCanceled();
            }
        }

        /// <summary>The first frame named <paramref name="name"/> matching <paramref name="predicate"/>.</summary>
        public async Task<JObject> WaitForAsync(string name, Func<JObject, bool> predicate, TimeSpan timeout, CancellationToken ct = default)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;

            while (true)
            {
                lock (gate)
                {
                    var match = frames.FirstOrDefault(f => f.Name == name && predicate(f.Data));
                    if (match.Data is not null)
                        return match.Data;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    string seen;
                    lock (gate)
                        seen = string.Join(", ", frames.Select(f => f.Name));
                    Assert.Fail($"no '{name}' frame matching the predicate within {timeout.TotalSeconds:F0}s; saw: [{seen}]");
                }

                await Task.Delay(50, ct);
            }
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
}
