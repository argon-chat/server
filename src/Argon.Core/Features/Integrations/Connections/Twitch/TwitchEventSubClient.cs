namespace Argon.Features.Integrations.Connections.Twitch;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Argon.Services;

/// <summary>A live stream as Helix describes it.</summary>
public sealed record TwitchStream(string BroadcasterId, string Login, string DisplayName, string Title, string Game, DateTimeOffset StartedAt)
{
    public string Url => $"https://twitch.tv/{Login}";
}

/// <summary>What arrived at the webhook, verbatim: the five headers Twitch sets and the raw body.</summary>
public sealed record TwitchEventSubMessage(
    string MessageId,
    string MessageType,
    string Timestamp,
    string Signature,
    string SubscriptionType,
    string Body);

/// <summary>What the webhook answers Twitch with.</summary>
public sealed record EventSubAnswer(int StatusCode, string? Body)
{
    public static readonly EventSubAnswer Accepted  = new(204, null);
    public static readonly EventSubAnswer Forbidden = new(403, null);
}

/// <summary>
/// The EventSub subscriptions and the stream read, with the application token both need.
/// </summary>
/// <remarks>
/// <para>An app access token (client credentials) rather than the streamer's own: <c>stream.online</c>
/// and <c>stream.offline</c> are public events that need no user scope, and Twitch requires webhook
/// subscriptions to be made with an app token anyway. It lives in the cache under
/// <c>conn:twitch:app-token</c> for a little less than Twitch says it lasts.</para>
///
/// <para>Subscription ids are not stored: Helix lists subscriptions by broadcaster, so removing a
/// person's two subscriptions is a list and two deletes, and nothing on the row has to stay in
/// step with Twitch.</para>
/// </remarks>
[ExcludeFromCodeCoverage(Justification = "Vendor HTTP adapter; the signature check and the parsers are exercised in the fast suite.")]
public sealed class TwitchEventSubClient(HttpClient http, IArgonCacheDatabase cache, IOptions<ConnectionsOptions> options)
{
    private const string Helix     = "https://api.twitch.tv/helix";
    private const string TokenUrl  = "https://id.twitch.tv/oauth2/token";
    private const string AppToken  = "conn:twitch:app-token";

    public static readonly string[] SubscriptionTypes = ["stream.online", "stream.offline"];

    private TwitchConnectionOptions Twitch => options.Value.Twitch;

    public string CallbackUrl => $"{options.Value.PublicCallbackBase.TrimEnd('/')}/connections/webhooks/twitch";

    /// <summary>Both subscriptions for a broadcaster; one that exists already (409) is left as it is.</summary>
    public async Task EnsureSubscriptionsAsync(string broadcasterId, CancellationToken ct)
    {
        var token = await AppTokenAsync(ct);

        foreach (var type in SubscriptionTypes)
        {
            using var request = Authorized(HttpMethod.Post, $"{Helix}/eventsub/subscriptions", token);

            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                type,
                version   = "1",
                condition = new { broadcaster_user_id = broadcasterId },
                transport = new { method = "webhook", callback = CallbackUrl, secret = Twitch.EventSubSecret }
            }), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
                continue;

            throw new ProviderCallException(response.StatusCode, $"{Helix}/eventsub/subscriptions", await response.Content.ReadAsStringAsync(ct));
        }
    }

    public async Task RemoveSubscriptionsAsync(string broadcasterId, CancellationToken ct)
    {
        var token = await AppTokenAsync(ct);

        using var list = Authorized(HttpMethod.Get, $"{Helix}/eventsub/subscriptions?user_id={Uri.EscapeDataString(broadcasterId)}", token);
        using var listed = await http.SendAsync(list, ct);

        var body = await listed.Content.ReadAsStringAsync(ct);

        if (!listed.IsSuccessStatusCode)
            throw new ProviderCallException(listed.StatusCode, $"{Helix}/eventsub/subscriptions", body);

        foreach (var id in ParseSubscriptionIds(body))
        {
            using var delete  = Authorized(HttpMethod.Delete, $"{Helix}/eventsub/subscriptions?id={Uri.EscapeDataString(id)}", token);
            using var deleted = await http.SendAsync(delete, ct);

            if (!deleted.IsSuccessStatusCode && deleted.StatusCode != HttpStatusCode.NotFound)
                throw new ProviderCallException(deleted.StatusCode, $"{Helix}/eventsub/subscriptions", await deleted.Content.ReadAsStringAsync(ct));
        }
    }

    /// <summary>The stream, or null when the channel is offline.</summary>
    public async Task<TwitchStream?> GetStreamAsync(string broadcasterId, CancellationToken ct)
    {
        var token = await AppTokenAsync(ct);

        using var request  = Authorized(HttpMethod.Get, $"{Helix}/streams?user_id={Uri.EscapeDataString(broadcasterId)}", token);
        using var response = await http.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new ProviderCallException(response.StatusCode, $"{Helix}/streams", body);

        using var document = JsonDocument.Parse(body);

        return ParseStream(document.RootElement);
    }

    public static TwitchStream? ParseStream(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;

        var stream = data[0];

        if (stream.String("type") is { } type && type != "live")
            return null;

        var id    = stream.String("user_id") ?? "";
        var login = stream.String("user_login") ?? id;

        return new TwitchStream(id, login, stream.String("user_name") ?? login, stream.String("title") ?? "",
            stream.String("game_name") ?? "", stream.Date("started_at") ?? DateTimeOffset.UtcNow);
    }

    public static IEnumerable<string> ParseSubscriptionIds(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var subscription in data.EnumerateArray())
        {
            if (subscription.String("id") is { } id)
                yield return id;
        }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Client-Id", Twitch.ClientId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return request;
    }

    private async Task<string> AppTokenAsync(CancellationToken ct)
    {
        if (await cache.StringGetAsync(AppToken, ct) is { Length: > 0 } cached)
            return cached;

        using var response = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(
        [
            new("client_id", Twitch.ClientId),
            new("client_secret", Twitch.ClientSecret),
            new("grant_type", "client_credentials")
        ]), ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new ProviderCallException(response.StatusCode, TokenUrl, body);

        var answer = OAuthCodeFlow.ParseToken(body, TokenUrl);
        var ttl    = TimeSpan.FromSeconds(Math.Max(60, (answer.ExpiresIn ?? 3600) - 3600));

        await cache.StringSetAsync(AppToken, answer.AccessToken, ttl, ct);

        return answer.AccessToken;
    }
}

/// <summary>The webhook's envelope: Twitch's signature over id, timestamp and body, and what the body says.</summary>
public static class TwitchEventSub
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// <c>sha256=HMAC-SHA256(secret, id ‖ timestamp ‖ body)</c>, compared in constant time, and the
    /// timestamp within ten minutes of now — the replay window Twitch's own guide names.
    /// </summary>
    public static bool VerifySignature(string secret, TwitchEventSubMessage message, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(message.Signature) || string.IsNullOrEmpty(message.MessageId))
            return false;

        if (!DateTimeOffset.TryParse(message.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
         || (now - at).Duration() > MaxAge)
            return false;

        var payload  = Encoding.UTF8.GetBytes(message.MessageId + message.Timestamp + message.Body);
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant();

        var left  = Encoding.UTF8.GetBytes(expected);
        var right = Encoding.UTF8.GetBytes(message.Signature.Trim().ToLowerInvariant());

        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    public static string? Challenge(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.String("challenge");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The subscription type and the broadcaster a notification is about.</summary>
    public static (string Type, string BroadcasterId)? Notification(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            var root = document.RootElement;
            var type = root.Object("subscription")?.String("type");
            var id   = root.Object("event")?.String("broadcaster_user_id")
                    ?? root.Object("subscription")?.Object("condition")?.String("broadcaster_user_id");

            return type is null || id is null ? null : (type, id);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
