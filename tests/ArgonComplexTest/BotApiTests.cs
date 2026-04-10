namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

/// <summary>
/// Integration tests for all stable Bot API interfaces (v1).
/// Seeds a real bot + user + space + channel in the DB, then calls HTTP endpoints.
/// </summary>
[TestFixture, Parallelizable(ParallelScope.Self)]
public class BotApiTests : TestBase
{
    // Shared state seeded once for all tests in this fixture
    private string  _botToken   = null!;
    private Guid    _botAppId;
    private Guid    _botUserId;
    private Guid    _ownerUserId;
    private Guid    _spaceId;
    private Guid    _textChannelId;
    private string  _ownerToken = null!;

    private HttpClient BotHttp => HttpClient;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // ───────────── Setup ─────────────

    [OneTimeSetUp]
    public async Task SetupBot()
    {
        // NUnit already called TestBase.OneTimeSetup() (containers + server host)

        // 1. Register a real user (the "owner") via Ion RPC
        _ownerToken = await RegisterAndGetTokenAsync();
        SetAuthToken(_ownerToken);

        // 2. Create a space as the owner
        _spaceId = await CreateSpaceAndGetIdAsync();

        // 3. Create a text channel
        _textChannelId = await CreateTextChannelAsync(_spaceId, "bot-test-channel");

        // 4. Get owner's userId
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        var ownerUser = await GetUserService(scope1.ServiceProvider).GetMe();
        _ownerUserId = ownerUser.userId;

        // 5. Seed bot entities directly in DB
        await SeedBotAsync();

        // 6. Join the bot to the space
        await JoinBotToSpaceAsync();
    }

    private async Task SeedBotAsync()
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
            .CreateDbContext();

        _botUserId = Guid.NewGuid();
        _botAppId  = Guid.NewGuid();
        _botToken  = GenerateTestBotToken(_botAppId);

        // Create the user entity that the bot impersonates
        db.Users.Add(new UserEntity
        {
            Id          = _botUserId,
            Username    = $"test_bot_{_botUserId:N}".Substring(0, 32),
            DisplayName = "Test Bot",
            Email       = $"bot_{_botUserId:N}@test.local",
            AgreeTOS    = true,
            DateOfBirth = new DateOnly(2000, 1, 1)
        });

        // Create dev team
        var teamId = Guid.NewGuid();
        db.TeamEntities.Add(new DevTeamEntity
        {
            TeamId = teamId,
            OwnerId = _ownerUserId,
            Name = "Test Team"
        });
        db.MemberTeamEntities.Add(new DevTeamMemberEntity
        {
            TeamId = teamId, UserId = _ownerUserId,
            JoinedAt = DateTime.UtcNow, IsOwner = true
        });

        // Create bot entity
        db.BotEntities.Add(new BotEntity
        {
            AppId          = _botAppId,
            TeamId         = teamId,
            Name           = "Test Bot App",
            ClientId       = Guid.NewGuid().ToString(),
            ClientSecret   = Guid.NewGuid().ToString(),
            AppType        = DevAppType.Bot,
            BotToken       = _botToken,
            BotAsUserId    = _botUserId,
            IsPublic       = true,
            MaxSpaces      = 100,
            RequiredScopes  = [],
            AllowedRedirects = []
        });

        await db.SaveChangesAsync();
        await db.DisposeAsync();
    }

    private async Task JoinBotToSpaceAsync()
    {
        // Use Orleans grain to join the bot-as-user to the space
        var section = Orleans.Runtime.RequestContext.AllowCallChainReentrancy();
        Orleans.Runtime.RequestContext.Set("$caller_user_id", _botUserId);
        Orleans.Runtime.RequestContext.Set("$caller_user_ip", "127.0.0.1");
        Orleans.Runtime.RequestContext.Set("$caller_machine_id", $"bot:{_botAppId}");

        try
        {
            var grains = FactoryAsp.Services.GetRequiredService<IGrainFactory>();
            await grains.GetGrain<ISpaceGrain>(_spaceId).DoJoinUserAsync();
        }
        finally
        {
            Orleans.Runtime.RequestContext.Clear();
        }
    }

    private static string GenerateTestBotToken(Guid botId)
    {
        Span<byte> botBytes = stackalloc byte[16];
        botId.TryWriteBytes(botBytes);
        botBytes.Reverse();

        Span<byte> secretBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(secretBytes);

        var secret = Convert.ToBase64String(secretBytes)
           .Replace('+', '-')
           .Replace('/', '_')
           .TrimEnd('=');

        return $"{Convert.ToHexString(botBytes)}:{secret}";
    }

    // ───────────── HTTP helpers ─────────────

    private HttpRequestMessage BotRequest(HttpMethod method, string path, object? body = null)
    {
        var msg = new HttpRequestMessage(method, $"/api/bot{path}");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bot", _botToken);
        if (body is not null)
        {
            msg.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOpts),
                Encoding.UTF8, "application/json");
        }
        return msg;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Body)> BotCallAsync(
        HttpMethod method, string path, object? body = null)
    {
        using var req = BotRequest(method, path, body);
        var resp = await BotHttp.SendAsync(req);
        JsonElement result = default;
        if (resp.IsSuccessStatusCode)
        {
            result = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        }
        return (resp, result);
    }

    private async Task<HttpResponseMessage> BotCallRawAsync(
        HttpMethod method, string path, object? body = null)
    {
        using var req = BotRequest(method, path, body);
        return await BotHttp.SendAsync(req);
    }

    // ───────────── IBotSelf/v1 ─────────────

    [Test, CancelAfter(60_000), Order(0)]
    public async Task BotSelf_GetMe_ReturnsProfile()
    {
        var (resp, me) = await BotCallAsync(HttpMethod.Get, "/IBotSelf/v1/GetMe");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(me.GetProperty("botId").GetString(), Is.EqualTo(_botAppId.ToString()));
        Assert.That(me.GetProperty("userId").GetString(), Is.EqualTo(_botUserId.ToString()));
        Assert.That(me.GetProperty("username").GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test, CancelAfter(60_000), Order(1)]
    public async Task BotSelf_GetSpaces_ReturnsJoinedSpace()
    {
        var (resp, data) = await BotCallAsync(HttpMethod.Get, "/IBotSelf/v1/GetSpaces");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var spaces = data.GetProperty("spaces");
        Assert.That(spaces.GetArrayLength(), Is.GreaterThanOrEqualTo(1));

        var found = false;
        foreach (var s in spaces.EnumerateArray())
        {
            if (s.GetProperty("spaceId").GetString() == _spaceId.ToString())
            {
                found = true;
                break;
            }
        }
        Assert.That(found, Is.True, "Bot's joined space not found in GetSpaces response");
    }

    [Test, CancelAfter(60_000), Order(2)]
    public async Task BotSelf_InvalidToken_Returns401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/bot/IBotSelf/v1/GetMe");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bot", "invalid-token-value");

        var resp = await BotHttp.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test, CancelAfter(60_000), Order(3)]
    public async Task BotSelf_NoAuthHeader_Returns401()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/bot/IBotSelf/v1/GetMe");
        // No Authorization header at all

        var resp = await BotHttp.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ───────────── ISpaces/v1 ─────────────

    [Test, CancelAfter(60_000), Order(10)]
    public async Task Spaces_Get_ReturnsSpaceDetail()
    {
        var (resp, data) = await BotCallAsync(
            HttpMethod.Get, $"/ISpaces/v1/Get?spaceId={_spaceId}");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(data.GetProperty("spaceId").GetString(), Is.EqualTo(_spaceId.ToString()));
        Assert.That(data.GetProperty("name").GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test, CancelAfter(60_000), Order(11)]
    public async Task Spaces_GetMember_ReturnsOwner()
    {
        var (resp, data) = await BotCallAsync(
            HttpMethod.Get, $"/ISpaces/v1/GetMember?spaceId={_spaceId}&userId={_ownerUserId}");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(data.GetProperty("userId").GetString(), Is.EqualTo(_ownerUserId.ToString()));
    }

    // ───────────── IChannels/v1 ─────────────

    [Test, CancelAfter(60_000), Order(20)]
    public async Task Channels_List_ReturnsChannels()
    {
        var (resp, data) = await BotCallAsync(
            HttpMethod.Get, $"/IChannels/v1/List?spaceId={_spaceId}");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var channels = data.GetProperty("channels");
        Assert.That(channels.GetArrayLength(), Is.GreaterThanOrEqualTo(1));
    }

    [Test, CancelAfter(60_000), Order(21)]
    public async Task Channels_Create_WithoutPermission_Returns403()
    {
        // Bot joined as regular member — no ManageChannels permission
        var resp = await BotCallRawAsync(
            HttpMethod.Post, "/IChannels/v1/Create",
            new { spaceId = _spaceId, name = "bot-created-chan", type = 0, description = "test" });

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test, CancelAfter(60_000), Order(22)]
    public async Task Channels_List_NonMemberSpace_Returns403()
    {
        var fakeSpaceId = Guid.NewGuid();
        var resp = await BotCallRawAsync(
            HttpMethod.Get, $"/IChannels/v1/List?spaceId={fakeSpaceId}");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ───────────── IMessages/v1 ─────────────

    [Test, CancelAfter(60_000), Order(30)]
    public async Task Messages_Send_ReturnsMessageId()
    {
        var (resp, data) = await BotCallAsync(
            HttpMethod.Post, "/IMessages/v1/Send",
            new
            {
                spaceId   = _spaceId,
                channelId = _textChannelId,
                text      = "Hello from bot test!",
                randomId  = Random.Shared.NextInt64()
            });

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(data.GetProperty("messageId").GetInt64(), Is.GreaterThan(0));
    }

    [Test, CancelAfter(60_000), Order(31)]
    public async Task Messages_History_ReturnsMessages()
    {
        // Ensure at least one message exists (from previous test)
        await BotCallAsync(
            HttpMethod.Post, "/IMessages/v1/Send",
            new
            {
                spaceId   = _spaceId,
                channelId = _textChannelId,
                text      = "Message for history test",
                randomId  = Random.Shared.NextInt64()
            });

        var (resp, data) = await BotCallAsync(
            HttpMethod.Get, $"/IMessages/v1/History?channelId={_textChannelId}&limit=10");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var messages = data.GetProperty("messages");
        Assert.That(messages.GetArrayLength(), Is.GreaterThanOrEqualTo(1));
    }

    [Test, CancelAfter(60_000), Order(32)]
    public async Task Messages_History_WithPagination_Works()
    {
        // Send 3 messages
        for (int i = 0; i < 3; i++)
        {
            await BotCallAsync(
                HttpMethod.Post, "/IMessages/v1/Send",
                new
                {
                    spaceId   = _spaceId,
                    channelId = _textChannelId,
                    text      = $"Pagination test #{i}",
                    randomId  = Random.Shared.NextInt64()
                });
        }

        // Get first page
        var (resp, data) = await BotCallAsync(
            HttpMethod.Get, $"/IMessages/v1/History?channelId={_textChannelId}&limit=2");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var messages = data.GetProperty("messages");
        Assert.That(messages.GetArrayLength(), Is.EqualTo(2));
    }

    // ───────────── ICommands/v1 ─────────────

    [Test, CancelAfter(60_000), Order(40)]
    public async Task Commands_RegisterAndList_Works()
    {
        // Register a global command
        var (regResp, regData) = await BotCallAsync(
            HttpMethod.Post, "/ICommands/v1/Register",
            new { name = "ping", description = "Pong!" });

        Assert.That(regResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var commandId = regData.GetProperty("commandId").GetString();
        Assert.That(commandId, Is.Not.Null.And.Not.Empty);

        // List all commands
        var (listResp, listData) = await BotCallAsync(
            HttpMethod.Get, "/ICommands/v1/List");

        Assert.That(listResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var commands = listData.GetProperty("commands");
        Assert.That(commands.GetArrayLength(), Is.GreaterThanOrEqualTo(1));
    }

    [Test, CancelAfter(60_000), Order(41)]
    public async Task Commands_Register_InvalidName_Returns400()
    {
        var resp = await BotCallRawAsync(
            HttpMethod.Post, "/ICommands/v1/Register",
            new { name = "", description = "Empty name" });

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.That(body.GetProperty("error").GetString(), Is.EqualTo("invalid_name"));
    }

    [Test, CancelAfter(60_000), Order(42)]
    public async Task Commands_Register_TooLongDescription_Returns400()
    {
        var resp = await BotCallRawAsync(
            HttpMethod.Post, "/ICommands/v1/Register",
            new { name = "test", description = new string('x', 101) });

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.That(body.GetProperty("error").GetString(), Is.EqualTo("invalid_description"));
    }

    [Test, CancelAfter(60_000), Order(43)]
    public async Task Commands_Update_ExistingCommand_Works()
    {
        // Register
        var (regResp, regData) = await BotCallAsync(
            HttpMethod.Post, "/ICommands/v1/Register",
            new { name = "updatable", description = "Before update" });
        Assert.That(regResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var commandId = regData.GetProperty("commandId").GetString();

        // Update
        var (updateResp, updateData) = await BotCallAsync(
            HttpMethod.Patch, "/ICommands/v1/Update",
            new { commandId, description = "After update" });

        Assert.That(updateResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(updateData.GetProperty("description").GetString(), Is.EqualTo("After update"));
    }

    [Test, CancelAfter(60_000), Order(44)]
    public async Task Commands_Delete_ExistingCommand_Works()
    {
        // Register
        var (regResp, regData) = await BotCallAsync(
            HttpMethod.Post, "/ICommands/v1/Register",
            new { name = "deletable", description = "Will be deleted" });
        Assert.That(regResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var commandId = regData.GetProperty("commandId").GetString();

        // Delete
        var deleteResp = await BotCallRawAsync(
            HttpMethod.Delete, $"/ICommands/v1/Delete?commandId={commandId}");
        Assert.That(deleteResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test, CancelAfter(60_000), Order(45)]
    public async Task Commands_Delete_NonExistent_Returns404()
    {
        var fakeId = Guid.NewGuid();
        var resp = await BotCallRawAsync(
            HttpMethod.Delete, $"/ICommands/v1/Delete?commandId={fakeId}");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test, CancelAfter(60_000), Order(46)]
    public async Task Commands_ListForSpace_Works()
    {
        // Register a space-scoped command
        await BotCallAsync(
            HttpMethod.Post, "/ICommands/v1/Register",
            new { name = "spaceonly", description = "Space scoped", spaceId = _spaceId });

        var (resp, data) = await BotCallAsync(
            HttpMethod.Get, $"/ICommands/v1/ListForSpace?spaceId={_spaceId}");

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var commands = data.GetProperty("commands");
        Assert.That(commands.GetArrayLength(), Is.GreaterThanOrEqualTo(1));
    }

    // ───────────── IMembers/v1 ─────────────

    [Test, CancelAfter(60_000), Order(50)]
    public async Task Members_Kick_NonExistentUser_Returns400()
    {
        var fakeUserId = Guid.NewGuid();
        var resp = await BotCallRawAsync(
            HttpMethod.Post,
            $"/IMembers/v1/Kick?spaceId={_spaceId}&channelId={_textChannelId}&userId={fakeUserId}");

        // Kicking a non-existent user should return 400 (kick_failed)
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ───────────── IVoice/v1 ─────────────

    [Test, CancelAfter(60_000), Order(60)]
    public async Task Voice_StreamToken_NonVoiceChannel_Returns400()
    {
        // _textChannelId is a text channel, not voice
        var resp = await BotCallRawAsync(
            HttpMethod.Post, "/IVoice/v1/StreamToken",
            new { spaceId = _spaceId, channelId = _textChannelId });

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.That(body.GetProperty("error").GetString(), Is.EqualTo("not_voice_channel"));
    }

    [Test, CancelAfter(60_000), Order(61)]
    public async Task Voice_StreamToken_NonExistentChannel_Returns404()
    {
        var fakeChannelId = Guid.NewGuid();
        var resp = await BotCallRawAsync(
            HttpMethod.Post, "/IVoice/v1/StreamToken",
            new { spaceId = _spaceId, channelId = fakeChannelId });

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.That(body.GetProperty("error").GetString(), Is.EqualTo("channel_not_found"));
    }

    [Test, CancelAfter(60_000), Order(62)]
    public async Task Voice_StreamToken_NonMemberSpace_Returns403()
    {
        var fakeSpaceId   = Guid.NewGuid();
        var fakeChannelId = Guid.NewGuid();
        var resp = await BotCallRawAsync(
            HttpMethod.Post, "/IVoice/v1/StreamToken",
            new { spaceId = fakeSpaceId, channelId = fakeChannelId });

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ───────────── IEvents/v1 — SSE Stream ─────────────

    /// <summary>
    /// Helper: opens SSE stream, collects events until predicate is met or timeout.
    /// Returns list of parsed SSE frames (event name, data JSON).
    /// </summary>
    private async Task<List<(string EventName, JObject Data)>> CollectSseEventsAsync(
        long? intents = null,
        Func<string, JObject, bool>? stopWhen = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout.Value);

        var url = "/api/bot/IEvents/v1/Stream";
        if (intents.HasValue)
            url += $"?intents={intents.Value}";

        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bot", _botToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var resp = await BotHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();

        var result = new List<(string, JObject)>();
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? currentEvent = null;
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token);
                if (line == null) break;

                if (line.StartsWith("event: "))
                    currentEvent = line[7..];
                else if (line.StartsWith("data: ") && currentEvent != null)
                {
                    var jo = JObject.Parse(line[6..]);
                    result.Add((currentEvent, jo));

                    if (stopWhen?.Invoke(currentEvent, jo) == true)
                        break;

                    currentEvent = null;
                }
            }
        }
        catch (OperationCanceledException) { }

        return result;
    }

    [Test, CancelAfter(30_000), Order(70)]
    public async Task Events_Stream_ReceivesReadyEvent()
    {
        var events = await CollectSseEventsAsync(
            stopWhen: (name, _) => name == "ready",
            timeout: TimeSpan.FromSeconds(10));

        Assert.That(events, Has.Count.GreaterThanOrEqualTo(1));

        var (readyName, readyData) = events.First(e => e.EventName == "ready");
        Assert.That(readyName, Is.EqualTo("ready"), "First event should be 'ready' (camelCase)");
        Assert.That(readyData["intents"], Is.Not.Null, "Ready event should contain intents");
        Assert.That(readyData["spaceIds"], Is.Not.Null, "Ready event should contain spaceIds");

        var spaceIds = readyData["spaceIds"]!.ToObject<List<string>>()!;
        Assert.That(spaceIds, Does.Contain(_spaceId.ToString()), "Ready event should include bot's space");
    }

    [Test, CancelAfter(30_000), Order(71)]
    public async Task Events_Stream_EventNames_AreCamelCase()
    {
        var events = await CollectSseEventsAsync(
            stopWhen: (name, _) => name == "ready",
            timeout: TimeSpan.FromSeconds(10));

        foreach (var (name, _) in events)
        {
            Assert.That(char.IsLower(name[0]), Is.True,
                $"Event name '{name}' should start with lowercase (camelCase)");
        }
    }

    [Test, CancelAfter(30_000), Order(72)]
    public async Task Events_Stream_ReadyData_HasNoUnionKey()
    {
        var events = await CollectSseEventsAsync(
            stopWhen: (name, _) => name == "ready",
            timeout: TimeSpan.FromSeconds(10));

        var (_, readyData) = events.First(e => e.EventName == "ready");

        Assert.That(readyData.ContainsKey("unionKey"), Is.False, "SSE data should not contain unionKey");
        Assert.That(readyData.ContainsKey("UnionKey"), Is.False, "SSE data should not contain UnionKey");
        Assert.That(readyData.ContainsKey("$type"), Is.False, "SSE data should not contain $type");
    }

    [Test, CancelAfter(60_000), Order(73)]
    public async Task Events_Stream_ReceivesMessageCreate_WithEntities()
    {
        // Start collecting events in background
        var eventTask = CollectSseEventsAsync(
            stopWhen: (name, _) => name == "messageCreate",
            timeout: TimeSpan.FromSeconds(15));

        // Wait a moment for the SSE connection to establish
        await Task.Delay(1000);

        // Send a message with entities via Bot API
        var sendResp = await BotCallRawAsync(
            HttpMethod.Post, "/IMessages/v1/Send",
            new
            {
                spaceId   = _spaceId,
                channelId = _textChannelId,
                text      = "Hello **bold** and *italic*!",
                entities  = new object[]
                {
                    new { type = 10, offset = 6, length = 4, version = 1 },   // Bold
                    new { type = 11, offset = 16, length = 6, version = 1 }   // Italic
                },
                randomId = Random.Shared.NextInt64()
            });
        Assert.That(sendResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var events = await eventTask;

        // Find messageCreate event
        var msgEvent = events.FirstOrDefault(e => e.EventName == "messageCreate");
        Assert.That(msgEvent.Data, Is.Not.Null, "Should receive messageCreate event");

        var data = msgEvent.Data;
        // Check the event payload structure
        var message = data["message"];
        Assert.That(message, Is.Not.Null, "messageCreate data should contain 'message'");
        Assert.That(message!["text"]?.Value<string>(), Is.EqualTo("Hello **bold** and *italic*!"));
        Assert.That(message["messageId"]?.Value<long>(), Is.GreaterThan(0));

        // Entities should be present and non-empty
        var entities = message["entities"] as JArray;
        Assert.That(entities, Is.Not.Null, "Message should have entities array");
        Assert.That(entities!.Count, Is.EqualTo(2), "Message should have 2 entities");

        // Bold entity preserved
        Assert.That(entities[0]["type"]?.Value<int>(), Is.EqualTo(10));
        Assert.That(entities[0]["offset"]?.Value<int>(), Is.EqualTo(6));
        Assert.That(entities[0]["length"]?.Value<int>(), Is.EqualTo(4));

        // No internal fields leaked
        Assert.That(data.ContainsKey("unionKey"), Is.False);
        Assert.That(data.ContainsKey("$type"), Is.False);
        Assert.That(entities[0].Value<string>("unionKey"), Is.Null);
    }

    [Test, CancelAfter(60_000), Order(74)]
    public async Task Events_Stream_MessageCreate_HasNoTypeMetadata()
    {
        var eventTask = CollectSseEventsAsync(
            stopWhen: (name, _) => name == "messageCreate",
            timeout: TimeSpan.FromSeconds(15));

        await Task.Delay(1000);

        await BotCallAsync(
            HttpMethod.Post, "/IMessages/v1/Send",
            new
            {
                spaceId   = _spaceId,
                channelId = _textChannelId,
                text      = "No metadata leak test",
                randomId  = Random.Shared.NextInt64()
            });

        var events = await eventTask;
        var msgEvent = events.FirstOrDefault(e => e.EventName == "messageCreate");
        Assert.That(msgEvent.Data, Is.Not.Null);

        // Serialize the whole event data to string and check for leaks
        var rawJson = msgEvent.Data.ToString(Newtonsoft.Json.Formatting.None);
        Assert.That(rawJson, Does.Not.Contain("$type"), "SSE event should not contain $type");
        Assert.That(rawJson, Does.Not.Contain("\"unionKey\""), "SSE event should not contain unionKey");
        Assert.That(rawJson, Does.Not.Contain("\"unionIndex\""), "SSE event should not contain unionIndex");
    }

    [Test, CancelAfter(30_000), Order(75)]
    public async Task Events_Stream_IntentFiltering_NoMessages()
    {
        // Connect with only Voice intent (2048) — should NOT receive messageCreate
        var eventTask = CollectSseEventsAsync(
            intents: 2048, // Voice only
            timeout: TimeSpan.FromSeconds(8));

        await Task.Delay(1000);

        await BotCallAsync(
            HttpMethod.Post, "/IMessages/v1/Send",
            new
            {
                spaceId   = _spaceId,
                channelId = _textChannelId,
                text      = "Should not appear",
                randomId  = Random.Shared.NextInt64()
            });

        var events = await eventTask;

        // Should have ready but not messageCreate
        Assert.That(events.Any(e => e.EventName == "ready"), Is.True);
        Assert.That(events.Any(e => e.EventName == "messageCreate"), Is.False,
            "messageCreate should be filtered when Messages intent is not set");
    }

    // ───────────── Path-based auth ─────────────

    [Test, CancelAfter(60_000), Order(80)]
    public async Task PathAuth_GetMe_ViaTokenInPath_Works()
    {
        // /api/bot/<token>/IBotSelf/v1/GetMe — no Authorization header
        var req = new HttpRequestMessage(HttpMethod.Get,
            $"/api/bot/{Uri.EscapeDataString(_botToken)}/IBotSelf/v1/GetMe");

        var resp = await BotHttp.SendAsync(req);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        Assert.That(body.GetProperty("botId").GetString(), Is.EqualTo(_botAppId.ToString()));
    }

    [Test, CancelAfter(60_000), Order(81)]
    public async Task PathAuth_InvalidToken_Returns401()
    {
        // Must match the regex pattern: 32 hex chars + : + base64url
        var req = new HttpRequestMessage(HttpMethod.Get,
            "/api/bot/00000000000000000000000000000000" + Uri.EscapeDataString(":AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA") + "/IBotSelf/v1/GetMe");

        var resp = await BotHttp.SendAsync(req);
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ───────────── Contract verification ─────────────

    [Test, CancelAfter(60_000), Order(90)]
    public async Task Contract_MetadataEndpoint_ListsInterfaces()
    {
        // GET /api/bot/ — anonymous, lists all interfaces
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/bot/");
        var resp = await BotHttp.SendAsync(req);

        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var interfaces = body.GetProperty("interfaces");
        Assert.That(interfaces.GetArrayLength(), Is.GreaterThanOrEqualTo(8));
    }

    [Test, CancelAfter(60_000), Order(91)]
    public async Task Contract_StableHashes_MatchAtRuntime()
    {
        var mismatches = Argon.Features.BotApi.BotContractVerifier.Verify();
        Assert.That(mismatches, Is.Empty, "Stable contract hash mismatches detected at runtime");
    }
}
