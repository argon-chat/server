namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argon.Core.Entities.Data;
using Argon.Entities;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;

/// <summary>
/// A bot seeded with the entitlements it asks for and installed the way a space owner installs one, which is what
/// grants them (the locked <c>Bot: {name}</c> role), and the HTTP calls it makes.
/// </summary>
internal static class BotHttpKit
{
    public sealed record Bot(Guid AppId, Guid UserId, string Token);

    public sealed record Answer(HttpStatusCode Status, JsonElement Body)
    {
        public string? Error => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("error", out var e) ? e.GetString() : null;

        public override string ToString() => $"{(int)Status} {Body}";
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<Bot> InstallAsync(TestUserSession owner, Guid spaceId, ArgonEntitlement required, CancellationToken ct,
        bool verified = false)
    {
        var bot       = await SeedAsync(owner, required, verified, ct);
        var installed = await SpaceGroupSupport.Bots(owner).InstallBot(spaceId, bot.AppId, ct);

        Assert.That(installed, Is.InstanceOf<SuccessInstallBot>(), $"could not install the bot: {(installed as FailedInstallBot)?.error}");
        return bot;
    }

    private static async Task<Bot> SeedAsync(TestUserSession developer, ArgonEntitlement required, bool verified, CancellationToken ct)
    {
        var botUserId = Guid.NewGuid();
        var botAppId  = Guid.NewGuid();
        var teamId    = Guid.NewGuid();
        var token     = Token(botAppId);
        var name      = SpaceGroupSupport.Unique("Kit");

        await using var db = await ChannelTestKit.DbAsync(ct);

        db.Users.Add(new UserEntity
        {
            Id          = botUserId,
            Username    = $"kbot_{botUserId:N}"[..32],
            DisplayName = name,
            Email       = $"kbot_{botUserId:N}@test.local",
            AgreeTOS    = true,
            DateOfBirth = new DateOnly(2000, 1, 1),
            BotEntityId = botAppId
        });
        db.TeamEntities.Add(new DevTeamEntity { TeamId = teamId, OwnerId = developer.UserId, Name = "Kit bot team" });
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
            LifecycleState       = BotLifecycleState.Published,
            IsVerified           = verified,
            MaxSpaces            = 100,
            RequiredEntitlements = required,
            RequiredScopes       = [],
            AllowedRedirects     = []
        });

        await db.SaveChangesAsync(ct);
        return new Bot(botAppId, botUserId, token);
    }

    private static string Token(Guid botAppId)
    {
        Span<byte> app = stackalloc byte[16];
        botAppId.TryWriteBytes(app);
        app.Reverse();

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"{Convert.ToHexString(app)}:{secret}";
    }

    public static async Task<Answer> SendAsync(Bot bot, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"/api/bot{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", bot.Token);

        using var response = await ArgonTestEnvironment.Instance.HttpClient.SendAsync(request, ct);
        var       text     = await response.Content.ReadAsStringAsync(ct);

        return new Answer(response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    public static Task<Answer> GetAsync(Bot bot, string path, CancellationToken ct)
        => SendAsync(bot, HttpMethod.Get, path, null, ct);

    public static Task<Answer> PostAsync(Bot bot, string path, object body, CancellationToken ct)
        => SendAsync(bot, HttpMethod.Post, path, new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"), ct);

    public static JsonElement Ok(Answer answer)
    {
        Assert.That(answer.Status, Is.EqualTo(HttpStatusCode.OK), answer.ToString());
        return answer.Body;
    }

    public static void Refused(Answer answer, HttpStatusCode status, string error, string? because = null)
    {
        Assert.That(answer.Status, Is.EqualTo(status), $"{because} {answer}");
        Assert.That(answer.Error, Is.EqualTo(error), $"{because} {answer}");
    }

    public static MultipartFormDataContent Part(MultipartFormDataContent form, string name, byte[] data, string contentType, string fileName)
    {
        var part = new ByteArrayContent(data);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, name, fileName);
        return form;
    }
}
